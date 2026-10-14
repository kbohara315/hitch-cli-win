using System.Diagnostics;
using Hitch.Native;

namespace Hitch.Sessions;

public sealed record ManagedShell(string Executable, string Arguments)
{
    public static ManagedShell Resolve(string? requested)
    {
        if (OperatingSystem.IsLinux())
        {
            var linuxCandidate = requested ?? Environment.GetEnvironmentVariable("HITCH_SHELL") ?? "bash";
            var linuxExecutable = ResolveExecutable(linuxCandidate);
            var linuxShellName = Path.GetFileName(linuxExecutable);
            var linuxArguments = linuxShellName switch
            {
                "bash" => "--noprofile -i",
                "zsh" => "-f -i",
                "fish" => "--no-config -i",
                _ => "-i"
            };
            return new ManagedShell(linuxExecutable, linuxArguments);
        }
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Hitch shell selection supports Windows and Linux only.");

        var candidate = requested
            ?? Environment.GetEnvironmentVariable("HITCH_SHELL")
            ?? FindInvokingShell()
            ?? FindPowerShell()
            ?? Environment.GetEnvironmentVariable("ComSpec")
            ?? "cmd.exe";
        var executable = ResolveExecutable(candidate);
        var name = Path.GetFileNameWithoutExtension(executable);
        var arguments = name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ? "/Q" : "-NoLogo";
        return new ManagedShell(executable, arguments);
    }

    private static string? FindInvokingShell()
    {
        var parentId = ProcessTreeInspector.GetParentProcessId(Environment.ProcessId);
        if (parentId is null)
            return null;
        try
        {
            using var parent = Process.GetProcessById(parentId.Value);
            var path = parent.MainModule?.FileName;
            if (path is null)
                return null;
            var name = Path.GetFileNameWithoutExtension(path);
            return name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                         or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindPowerShell()
    {
        foreach (var name in new[] { "pwsh.exe", "powershell.exe" })
        {
            var found = FindOnPath(name);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static string ResolveExecutable(string candidate)
    {
        if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            return Path.GetFullPath(candidate);
        return FindOnPath(candidate) ?? throw new FileNotFoundException($"Could not find shell executable '{candidate}' on PATH.");
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
