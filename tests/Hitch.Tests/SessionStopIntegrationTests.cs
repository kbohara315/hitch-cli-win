using System.Diagnostics;
using System.Text;
using Xunit;

namespace Hitch.Tests;

public sealed class SessionStopIntegrationTests
{
    [WindowsFact]
    public async Task ManagedStopPathsEndTheHostWithoutReturningToTheManagedPrompt()
    {
        var hitch = FindHitchExecutable();
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var builtInOffOutput = await RunManagedSessionAsync(hitch, shell, Encoding.UTF8.GetBytes("hitch off\r\n"));
        AssertStopsWithoutPrompt(builtInOffOutput);

        var executableOffOutput = await RunManagedSessionAsync(hitch, shell,
            Encoding.UTF8.GetBytes("& $env:HITCH_EXE off\r\n"));
        AssertStopsWithoutPrompt(executableOffOutput);

        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var cmdOffOutput = await RunManagedSessionAsync(hitch, cmd, Encoding.UTF8.GetBytes("hitch off\r\n"));
        AssertStopsWithoutPrompt(cmdOffOutput);

        var detachOutput = await RunManagedSessionAsync(hitch, shell, [0x1c]);
        Assert.Contains("Hitch session", detachOutput, StringComparison.Ordinal);
    }

    private static void AssertStopsWithoutPrompt(string output)
    {
        var stopMessage = output.IndexOf("Stopping shared session.", StringComparison.Ordinal);
        Assert.True(stopMessage >= 0, output);
        var afterStop = output[(stopMessage + "Stopping shared session.".Length)..];
        Assert.DoesNotMatch("(?m)#\\d+ (?:PS |[A-Z]:)", afterStop);
    }

    private static async Task<string> RunManagedSessionAsync(string hitch, string shell, byte[] input)
    {
        var start = CreateHostStartInfo(hitch, shell);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch Hitch test host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(300);
            await process.StandardInput.BaseStream.WriteAsync(input);
            await process.StandardInput.BaseStream.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            return await stdout + await stderr;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static ProcessStartInfo CreateHostStartInfo(string hitch, string shell)
    {
        var start = new ProcessStartInfo
        {
            FileName = hitch,
            Arguments = $"on --shell \"{shell}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var name in new[] { "HITCH_SESSION", "HITCH_PIPE", "HITCH_HOST_PID", "HITCH_EXE", "HITCH_CWD_SYNC_FILE" })
            start.EnvironmentVariables.Remove(name);
        return start;
    }

    private static string FindHitchExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var output = Path.Combine(directory.FullName, "src", "Hitch", "bin");
            if (Directory.Exists(output))
            {
                var candidate = Directory.GetFiles(output, "hitch.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}win-x64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
                if (candidate is not null) return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate the Hitch apphost for session stop integration tests.");
    }
}
