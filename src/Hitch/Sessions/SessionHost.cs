using System.Diagnostics;
using Hitch.Native;

namespace Hitch.Sessions;

public sealed class SessionHost(SessionStore store)
{
    public async Task<int> RunAsync(string shell, string shellArguments, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Hitch session hosting supports Windows and Linux only.");
        var id = store.AllocateId();
        var pipeName = OperatingSystem.IsLinux()
            ? Path.Combine(store.SessionDirectory(id), "control.sock")
            : "hitch-" + id;
        var startedAt = DateTimeOffset.UtcNow;
        var syncPath = Environment.GetEnvironmentVariable("HITCH_CWD_SYNC_FILE");
        var cwd = Environment.CurrentDirectory;
        var size = ConsoleModeScope.GetSize();
        var redirectShellOutput = Console.IsOutputRedirected;
        using var terminalMode = ConsoleModeScope.Enter();
        var hostExecutable = ResolveHostExecutable();
        var cmdShimPath = Path.Combine(store.SessionDirectory(id), OperatingSystem.IsLinux() ? "hitch" : "hitch.cmd");
        var shim = OperatingSystem.IsLinux()
            ? "#!/bin/sh\nexec \"$HITCH_EXE\" \"$@\"\n"
            : "@echo off\r\n\"%HITCH_EXE%\" %*\r\nexit /b %ERRORLEVEL%\r\n";
        await File.WriteAllTextAsync(cmdShimPath, shim, cancellationToken);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(cmdShimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        if (OperatingSystem.IsLinux() && Path.GetFileName(shell) == "bash")
        {
            var bootstrapPath = Path.Combine(store.SessionDirectory(id), "bashrc");
            await File.WriteAllTextAsync(bootstrapPath, ManagedShellPrompt.BashBootstrap, cancellationToken);
            shellArguments += $" --rcfile \"{bootstrapPath}\"";
        }
        else if (IsPowerShell(shell))
        {
            var bootstrapPath = Path.Combine(store.SessionDirectory(id), "prompt.ps1");
            await File.WriteAllTextAsync(bootstrapPath, ManagedShellPrompt.PowerShellBootstrap, cancellationToken);
            shellArguments += $" -NoExit -File \"{bootstrapPath}\"";
        }

        var oldSession = Environment.GetEnvironmentVariable("HITCH_SESSION");
        var oldPipe = Environment.GetEnvironmentVariable("HITCH_PIPE");
        var oldHostPid = Environment.GetEnvironmentVariable("HITCH_HOST_PID");
        var oldExecutable = Environment.GetEnvironmentVariable("HITCH_EXE");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldPrompt = Environment.GetEnvironmentVariable("PROMPT");
        Environment.SetEnvironmentVariable("HITCH_SESSION", id);
        Environment.SetEnvironmentVariable("HITCH_PIPE", pipeName);
        Environment.SetEnvironmentVariable("HITCH_HOST_PID", Environment.ProcessId.ToString());
        Environment.SetEnvironmentVariable("HITCH_EXE", hostExecutable);
        Environment.SetEnvironmentVariable("PATH", store.SessionDirectory(id) + Path.PathSeparator + oldPath);
        if (Path.GetFileNameWithoutExtension(shell).Equals("cmd", StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PROMPT", "$E]7;file://localhost/$P$E\\$S$E[32m#" + id + "$E[0m$S" + (oldPrompt ?? "$P$G"));
        try
        {
            await using IManagedTerminalSession shellSession = OperatingSystem.IsLinux()
                ? LinuxPtySession.Start(shell, shellArguments, cwd, size.Columns, size.Rows, Path.Combine(store.SessionDirectory(id), "resize.sock"), hostExecutable)
                : ConPtySession.Start(shell, shellArguments, cwd, size.Columns, size.Rows, redirectStandardOutput: redirectShellOutput);
            var record = new SessionRecord(id, Environment.ProcessId, shell, cwd, pipeName, startedAt);
            store.Save(record);
            var activity = new SessionActivityTracker(store.ReadState(id));
            await using var outputLog = new BoundedOutputLog(store.OutputPath(id));
            await using var activeLog = new BoundedOutputLog(store.ActiveOutputPath(id), 8 * 1024 * 1024, 7 * 1024 * 1024);
            var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pipeServer = new SessionPipeServer(pipeName, id, store, shellSession, outputLog, activeLog, activity,
                () =>
                {
                    shellSession.KillTree();
                    stop.TrySetResult();
                });
            Console.WriteLine($"Hitch session {id} · {Path.GetFileName(shell)}");
            Console.WriteLine("Agent commands can now inspect and control this session. Press Ctrl+\\ or run `hitch off` to stop it.");

            var outputTask = PumpOutputAsync(shellSession, outputLog, activeLog, store, activity, id, shutdown.Token);
            var inputTask = PumpInputAsync(shellSession, store, activity, id, shutdown.Token);
            var pipeTask = pipeServer.RunAsync(shutdown.Token);
            var monitorTask = MonitorStateAsync(shellSession, Path.GetFileNameWithoutExtension(shell), store, activity, id, activeLog, shutdown.Token);
            var resizeTask = MonitorResizeAsync(shellSession, shutdown.Token);
            var exitTask = shellSession.WaitForExitAsync(shutdown.Token);

            var exitCode = await SessionLifecycle.RunAsync(
                exitTask,
                inputTask,
                stop.Task,
                () => { if (!shellSession.HasExited) shellSession.KillTree(); },
                shellSession.ClosePseudoConsole,
                outputTask,
                shutdown.Cancel,
                [pipeTask, monitorTask, resizeTask],
                shellSession.DisposeAsync);

            var finalState = store.ReadState(id);
            if (!string.IsNullOrWhiteSpace(syncPath) && Directory.Exists(finalState.CurrentDirectory))
                await File.WriteAllTextAsync(syncPath, finalState.CurrentDirectory, cancellationToken);
            return exitCode;
        }
        finally
        {
            Environment.SetEnvironmentVariable("HITCH_SESSION", oldSession);
            Environment.SetEnvironmentVariable("HITCH_PIPE", oldPipe);
            Environment.SetEnvironmentVariable("HITCH_HOST_PID", oldHostPid);
            Environment.SetEnvironmentVariable("HITCH_EXE", oldExecutable);
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("PROMPT", oldPrompt);
            store.Delete(id);
        }
    }

    private static bool IsPowerShell(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
               || name.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveHostExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileNameWithoutExtension(processPath).Equals("hitch", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsLinux() ? "hitch" : "hitch.exe");
        if (File.Exists(appHost))
            return appHost;
        throw new InvalidOperationException("Could not locate the Hitch executable to expose session commands to the managed shell.");
    }

    private static async Task PumpOutputAsync(
        IManagedTerminalSession shell,
        BoundedOutputLog outputLog,
        BoundedOutputLog activeLog,
        SessionStore store,
        SessionActivityTracker activity,
        string id,
        CancellationToken cancellationToken)
    {
        var stdout = Console.OpenStandardOutput();
        var parser = new CwdMarkerParser();
        await shell.PumpOutputAsync(async bytes =>
        {
            await stdout.WriteAsync(bytes, cancellationToken);
            await stdout.FlushAsync(cancellationToken);
            await outputLog.AppendAsync(bytes, cancellationToken);
            var cwd = parser.Observe(bytes.Span);
            if (cwd is not null)
            {
                store.SaveState(id, activity.PromptObserved(cwd, DateTimeOffset.UtcNow));
            }
            if (activity.Current.CommandRunning)
                await activeLog.AppendAsync(bytes, cancellationToken);
        }, cancellationToken);
    }

    private static async Task PumpInputAsync(IManagedTerminalSession shell, SessionStore store, SessionActivityTracker activity,
        string sessionId, CancellationToken cancellationToken)
    {
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stdin.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    return;
                if (buffer.AsSpan(0, read).Contains((byte)0x1c))
                {
                    shell.KillTree();
                    return;
                }
                if (buffer.AsSpan(0, read).Contains((byte)'\r'))
                    MarkCommandSubmitted(store, activity, sessionId);
                await shell.WriteInputAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private static async Task MonitorStateAsync(
        IManagedTerminalSession shell,
        string shellName,
        SessionStore store,
        SessionActivityTracker activity,
        string id,
        BoundedOutputLog activeLog,
        CancellationToken cancellationToken)
    {
        bool wasRunning = false;
        while (!cancellationToken.IsCancellationRequested && !shell.HasExited)
        {
            var active = SelectActiveProcess(ProcessTreeInspector.GetDescendantNames(shell.ProcessId), shellName);
            var old = activity.Current;
            var running = active is not null || !old.AtPrompt;
            if (running && !wasRunning)
                await File.WriteAllBytesAsync(store.ActiveOutputPath(id), [], cancellationToken);
            var observed = activity.ProcessObserved(active, DateTimeOffset.UtcNow);
            if (!ReferenceEquals(observed, old))
                store.SaveState(id, observed);
            wasRunning = running;
            await Task.Delay(400, cancellationToken);
        }
    }

    internal static string? SelectActiveProcess(IEnumerable<string> descendants, string shellName)
        => descendants.FirstOrDefault(name => !name.Equals("hitch", StringComparison.OrdinalIgnoreCase)
                                              && !name.Equals("hitch-pty", StringComparison.OrdinalIgnoreCase)
                                              && !name.Equals("conhost", StringComparison.OrdinalIgnoreCase)
                                              && !(OperatingSystem.IsLinux() && name.Equals(shellName, StringComparison.OrdinalIgnoreCase)));

    private static void MarkCommandSubmitted(SessionStore store, SessionActivityTracker activity, string id)
    {
        store.SaveState(id, activity.Submitted(DateTimeOffset.UtcNow));
    }

    private static async Task MonitorResizeAsync(IManagedTerminalSession shell, CancellationToken cancellationToken)
    {
        var previous = ConsoleModeScope.GetSize();
        while (!cancellationToken.IsCancellationRequested && !shell.HasExited)
        {
            await Task.Delay(250, cancellationToken);
            var current = ConsoleModeScope.GetSize();
            if (current != previous)
            {
                shell.Resize(current.Columns, current.Rows);
                previous = current;
            }
        }
    }
}
