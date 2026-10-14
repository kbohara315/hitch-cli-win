using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Hitch.Native;

internal sealed class LinuxPtySession : IManagedTerminalSession
{
    private readonly Process _process;
    private readonly string _controlPath;
    private bool _disposed;

    private LinuxPtySession(Process process, string controlPath)
    {
        _process = process;
        _controlPath = controlPath;
    }

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public static LinuxPtySession Start(string shell, string arguments, string workingDirectory, short columns, short rows,
        string controlPath, string hostExecutable, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "hitch-pty");
        if (!File.Exists(helper))
            throw new FileNotFoundException($"Linux PTY helper was not found at '{helper}'. Build or install the hitch-pty helper alongside Hitch.");
        var info = new ProcessStartInfo(helper)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        info.ArgumentList.Add("--control=" + controlPath);
        info.ArgumentList.Add(shell);
        foreach (var argument in SplitArguments(arguments))
            info.ArgumentList.Add(argument);
        info.Environment["HITCH_COLUMNS"] = Math.Max(1, (int)columns).ToString();
        info.Environment["HITCH_ROWS"] = Math.Max(1, (int)rows).ToString();
        info.Environment["HITCH_EXE"] = hostExecutable;
        if (environment is not null)
            foreach (var (name, value) in environment)
                if (value is null) info.Environment.Remove(name); else info.Environment[name] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the Linux PTY helper.");
        return new LinuxPtySession(process, controlPath);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => WaitCoreAsync(cancellationToken);
    private async Task<int> WaitCoreAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token);
        return _process.ExitCode;
    }

    public async Task WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
        await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }

    internal void CloseInput() => _process.StandardInput.Close();

    public async Task PumpOutputAsync(Func<ReadOnlyMemory<byte>, Task> onOutput, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await _process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            await onOutput(buffer.AsMemory(0, read));
        }
    }

    public void Resize(short columns, short rows)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(_controlPath));
            Span<byte> data = stackalloc byte[4];
            BitConverter.TryWriteBytes(data[..2], Math.Max((short)1, columns));
            BitConverter.TryWriteBytes(data[2..], Math.Max((short)1, rows));
            socket.Send(data);
        }
        catch (SocketException) { }
    }

    public void KillTree()
    {
        if (_process.HasExited) return;
        _ = kill(_process.Id, 15); // helper handles SIGTERM and sends SIGHUP to the entire shell process group
    }

    public void ClosePseudoConsole() { }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_process.HasExited)
        {
            KillTree();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { _process.Kill(entireProcessTree: true); }
        }
        _process.Dispose();
        try { File.Delete(_controlPath); } catch (IOException) { }
    }

    internal static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        char quote = '\0';
        bool escaped = false;
        foreach (var character in arguments)
        {
            if (escaped) { value.Append(character); escaped = false; continue; }
            if (character == '\\' && quote != '\'') { escaped = true; continue; }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else value.Append(character);
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (value.Length > 0) { result.Add(value.ToString()); value.Clear(); }
                continue;
            }
            value.Append(character);
        }
        if (escaped) value.Append('\\');
        if (quote != '\0') throw new ArgumentException("Unterminated quote in Linux shell arguments.", nameof(arguments));
        if (value.Length > 0) result.Add(value.ToString());
        return result;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
