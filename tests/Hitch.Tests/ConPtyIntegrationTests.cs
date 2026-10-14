using System.Text;
using Hitch.Native;
using Hitch.Sessions;
using Xunit;

namespace Hitch.Tests;

public sealed class ConPtyIntegrationTests
{
    [WindowsFact]
    public async Task ConPtyRunsCommandAndCapturesItsTerminalOutput()
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var arguments = "-NoLogo -NoProfile -Command \"Write-Output HITCH_CONPTY_OK\"";
        var session = ConPtySession.Start(shell, arguments, Environment.CurrentDirectory, 100, 30);
        using var output = new MemoryStream();
        using var readCancellation = new CancellationTokenSource();
        var outputTask = session.PumpOutputAsync(bytes =>
        {
            lock (output)
                output.Write(bytes.Span);
            return Task.CompletedTask;
        }, readCancellation.Token);

        try
        {
            await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            session.ClosePseudoConsole();
            await outputTask.WaitAsync(TimeSpan.FromSeconds(3));

            string text;
            lock (output)
                text = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("HITCH_CONPTY_OK", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            session.KillTree();
            readCancellation.Cancel();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [WindowsFact]
    public async Task ConPtyForwardsTypedInputToInteractiveCmd()
    {
        var shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var session = ConPtySession.Start(shell, "/Q", Environment.CurrentDirectory, 100, 30);
        using var output = new MemoryStream();
        using var readCancellation = new CancellationTokenSource();
        var outputTask = session.PumpOutputAsync(bytes =>
        {
            lock (output)
                output.Write(bytes.Span);
            return Task.CompletedTask;
        }, readCancellation.Token);

        try
        {
            await Task.Delay(150);
            await session.WriteInputAsync(Encoding.ASCII.GetBytes("echo HITCH_INTERACTIVE_OK\r\n"));
            await Task.Delay(150);
            await session.WriteInputAsync(Encoding.ASCII.GetBytes("exit\r\n"));
            await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            session.ClosePseudoConsole();
            await outputTask.WaitAsync(TimeSpan.FromSeconds(3));

            string text;
            lock (output)
                text = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("HITCH_INTERACTIVE_OK", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            session.KillTree();
            readCancellation.Cancel();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [WindowsFact]
    public async Task ManagedPowerShellPromptShowsItsSessionId()
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var root = Path.Combine(Path.GetTempPath(), "hitch-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "prompt.ps1");
        await File.WriteAllTextAsync(script, ManagedShellPrompt.PowerShellBootstrap);
        var previousSession = Environment.GetEnvironmentVariable("HITCH_SESSION");
        Environment.SetEnvironmentVariable("HITCH_SESSION", "1");
        var session = ConPtySession.Start(shell, $"-NoLogo -NoProfile -NoExit -File \"{script}\"", root, 100, 30);
        using var output = new MemoryStream();
        using var readCancellation = new CancellationTokenSource();
        var outputTask = session.PumpOutputAsync(bytes =>
        {
            lock (output)
                output.Write(bytes.Span);
            return Task.CompletedTask;
        }, readCancellation.Token);

        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
            string text;
            do
            {
                lock (output)
                    text = Encoding.UTF8.GetString(output.ToArray());
                if (text.Contains("#1", StringComparison.Ordinal))
                    break;
                await Task.Delay(40);
            } while (DateTimeOffset.UtcNow < deadline);

            Assert.Contains("\u001b[32m#1\u001b[0m", text, StringComparison.Ordinal);
        }
        finally
        {
            session.KillTree();
            try { await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
            session.ClosePseudoConsole();
            readCancellation.Cancel();
            try { await outputTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            await session.DisposeAsync();
            Environment.SetEnvironmentVariable("HITCH_SESSION", previousSession);
            Directory.Delete(root, recursive: true);
        }
    }
}
