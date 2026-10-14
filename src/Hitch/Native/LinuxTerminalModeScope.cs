using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Hitch.Native;

internal sealed class LinuxTerminalModeScope : IDisposable
{
    private readonly string? _savedMode;
    private readonly PosixSignalRegistration? _term;
    private readonly PosixSignalRegistration? _hangup;
    private bool _disposed;

    private LinuxTerminalModeScope()
    {
        if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
        {
            _savedMode = RunStty("-g");
            _ = RunStty("raw -echo");
            _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Restore());
            _hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Restore());
        }
    }

    public static LinuxTerminalModeScope Enter() => new();

    public static (short Columns, short Rows) GetSize()
    {
        if (Console.IsOutputRedirected) return (120, 30);
        try
        {
            var value = RunStty("size").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return value.Length == 2 && short.TryParse(value[1], out var columns) && short.TryParse(value[0], out var rows)
                ? ((short)Math.Max(1, (int)columns), (short)Math.Max(1, (int)rows)) : ((short)120, (short)30);
        }
        catch (InvalidOperationException) { return (120, 30); }
    }

    private static string RunStty(string arguments)
    {
        var info = new ProcessStartInfo("stty", arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("Could not start stty to configure the terminal."); }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Hitch requires the 'stty' utility (provided by coreutils) to manage terminal mode safely.", exception);
        }
        using (process)
        {
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Could not configure the terminal with stty: {error.Trim()}");
        return output.Trim();
        }
    }

    private void Restore()
    {
        if (_disposed) return;
        _disposed = true;
        if (_savedMode is not null)
        {
            try { _ = RunStty(_savedMode); } catch (InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        Restore();
        _term?.Dispose();
        _hangup?.Dispose();
    }
}
