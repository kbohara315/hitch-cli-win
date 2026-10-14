using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Security.Cryptography;
using Hitch.Native;
using Hitch.Setup;
using Hitch.Sessions;
using Xunit;

namespace Hitch.Tests;

public sealed class LinuxRuntimeTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    [Fact]
    public void BashBootstrapUsesOsc7AndPreservesPromptCommandWithoutAccumulatingPrefix()
    {
        var bootstrap = ManagedShellPrompt.BashBootstrap;
        Assert.Contains("printf '\\033]7;file://localhost%s\\007'", bootstrap, StringComparison.Ordinal);
        Assert.Contains("__hitch_original_prompt_command", bootstrap, StringComparison.Ordinal);
        Assert.Contains("__hitch_base_ps1", bootstrap, StringComparison.Ordinal);
        Assert.Contains("source \"$HOME/.bashrc\"", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("HOSTNAME", bootstrap, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveProcessSelectionDoesNotTreatManagedBashAsACommand()
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Null(SessionHost.SelectActiveProcess(["bash"], "bash"));
        Assert.Equal("sleep", SessionHost.SelectActiveProcess(["bash", "sleep"], "bash"));
    }

    [Fact]
    public void LinuxShellArgumentsKeepGeneratedRcfilePathWithSpacesAsOneArgument()
    {
        var args = LinuxPtySession.SplitArguments("--noprofile -i --rcfile \"/home/user/My Projects/Hitch/bashrc\"");
        Assert.Equal(["--noprofile", "-i", "--rcfile", "/home/user/My Projects/Hitch/bashrc"], args);
    }

    [Fact]
    public void LinuxBashResolverDoesNotDisableTheExplicitRcfile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var shell = ManagedShell.Resolve("bash");
        Assert.Contains("--noprofile", shell.Arguments, StringComparison.Ordinal);
        Assert.Contains("-i", shell.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("--norc", shell.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxSessionDirectoriesAndStateFilesUseOwnerOnlyPermissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hitch-private-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        try
        {
            var id = store.AllocateId();
            var now = DateTimeOffset.UtcNow;
            store.Save(new SessionRecord(id, Environment.ProcessId, "bash", root, "unused", now));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.SessionDirectory(id)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.RecordPath(id)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.StatePath(id)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LinuxSessionIdLockExcludesACompetingProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hitch lock-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        var firstId = store.AllocateId();
        store.Delete(firstId);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))))[..16];
        var lockPath = Path.Combine(root, $".ids-{identity}.lock");
        var markerPath = Path.Combine(root, "lock-ready");
        try
        {
            var holderInfo = new ProcessStartInfo("python3") { UseShellExecute = false };
            holderInfo.ArgumentList.Add("-c");
            holderInfo.ArgumentList.Add("import fcntl,sys,time; f=open(sys.argv[1],'r+b'); fcntl.lockf(f,fcntl.LOCK_EX); open(sys.argv[2],'w').close(); time.sleep(0.8)");
            holderInfo.ArgumentList.Add(lockPath);
            holderInfo.ArgumentList.Add(markerPath);
            using var holder = Process.Start(holderInfo) ?? throw new InvalidOperationException("Could not launch the cross-process lock holder.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => File.Exists(markerPath), timeout.Token);
            var started = Stopwatch.GetTimestamp();
            Assert.Equal("1", store.AllocateId());
            Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(500));
            await holder.WaitForExitAsync(timeout.Token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LinuxSetupBacksUpExistingFilesAndIsIdempotentWithTheHelperAndSymlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hitch setup test-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home with spaces");
        var installRoot = Path.Combine(root, "install");
        var profile = Path.Combine(home, ".bashrc");
        var bin = Path.Combine(home, ".local", "bin");
        var commandPath = Path.Combine(bin, "hitch");
        var source = Path.Combine(root, "hitch source");
        Directory.CreateDirectory(bin);
        await File.WriteAllTextAsync(profile, "# user profile content\n");
        await File.WriteAllTextAsync(commandPath, "user command\n");
        await File.WriteAllTextAsync(source, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var installer = new ShellIntegrationInstaller(installRoot, home);
        try
        {
            Assert.True(await installer.InstallNonInteractiveAsync(source));
            var installed = Path.Combine(installRoot, "app", "hitch");
            var helper = Path.Combine(installRoot, "app", "hitch-pty");
            Assert.True(File.Exists(helper));
            Assert.Equal(Path.GetFullPath(installed), new FileInfo(commandPath).LinkTarget);
            Assert.Contains("# user profile content", await File.ReadAllTextAsync(profile), StringComparison.Ordinal);
            Assert.Contains("HITCH_CWD_SYNC_FILE", await File.ReadAllTextAsync(profile), StringComparison.Ordinal);
            var backups = Directory.GetFiles(Path.Combine(installRoot, "backups"));
            Assert.Contains(backups, path => Path.GetFileName(path).StartsWith("bin-hitch.", StringComparison.Ordinal));
            Assert.Contains(backups, path => Path.GetFileName(path).StartsWith("home with spaces-.bashrc.", StringComparison.Ordinal));
            var profileTime = File.GetLastWriteTimeUtc(profile);
            var backupCount = backups.Length;

            Assert.True(await installer.InstallNonInteractiveAsync(source));
            Assert.Equal(profileTime, File.GetLastWriteTimeUtc(profile));
            Assert.Equal(backupCount, Directory.GetFiles(Path.Combine(installRoot, "backups")).Length);
            Assert.True(File.Exists(helper));
            Assert.Equal(Path.GetFullPath(installed), new FileInfo(commandPath).LinkTarget);

            File.Delete(commandPath);
            File.CreateSymbolicLink(commandPath, "missing-user-hitch");
            Assert.True(await installer.InstallNonInteractiveAsync(source));
            Assert.Equal(Path.GetFullPath(installed), new FileInfo(commandPath).LinkTarget);
            Assert.Contains(Directory.GetFiles(Path.Combine(installRoot, "backups")), path =>
                Path.GetFileName(path).StartsWith("hitch-link-", StringComparison.Ordinal)
                && File.ReadAllText(path) == "missing-user-hitch");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LinuxPtySupportsResizeCtrlCAndCleanShellExit()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hitch pty test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var control = Path.Combine(root, "resize.sock");
        var bootstrap = Path.Combine(root, "bash startup.rc");
        await File.WriteAllTextAsync(bootstrap, ManagedShellPrompt.BashBootstrap);
        await File.WriteAllTextAsync(Path.Combine(root, ".bashrc"), "export HITCH_USER_RC_SEEN=loaded\n");
        await using var shell = LinuxPtySession.Start("/bin/bash", $"--noprofile -i --rcfile \"{bootstrap}\"", root,
            80, 24, control, "/bin/true", new Dictionary<string, string?> { ["HITCH_SESSION"] = "42", ["HOME"] = root });
        var output = new StringBuilder();
        var outputGate = new object();
        var cwdParser = new CwdMarkerParser();
        string? observedCwd = null;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pump = shell.PumpOutputAsync(bytes =>
        {
            lock (outputGate)
            {
                output.Append(Encoding.UTF8.GetString(bytes.Span));
                observedCwd = cwdParser.Observe(bytes.Span) ?? observedCwd;
            }
            return Task.CompletedTask;
        }, shutdown.Token);
        try
        {
            await WaitUntilAsync(() => ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("bash"), shutdown.Token);
            await WaitUntilAsync(() => observedCwd == root, shutdown.Token);
            Assert.Null(SessionHost.SelectActiveProcess(ProcessTreeInspector.GetDescendantNames(shell.ProcessId), "bash"));

            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("printf 'HITCH_RC=%s\\n' \"$HITCH_USER_RC_SEEN\"\n"), shutdown.Token);
            await WaitUntilAsync(() => { lock (outputGate) return output.ToString().Contains("HITCH_RC=loaded", StringComparison.Ordinal); }, shutdown.Token);

            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("sleep 8\n"), shutdown.Token);
            await WaitUntilAsync(() => ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("sleep"), shutdown.Token);
            Assert.Equal("sleep", SessionHost.SelectActiveProcess(ProcessTreeInspector.GetDescendantNames(shell.ProcessId), "bash"));

            shell.Resize(100, 40);
            await shell.WriteInputAsync(new byte[] { 0x03 }, shutdown.Token);
            await WaitUntilAsync(() => !ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("sleep"), shutdown.Token);
            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("stty size\n"), shutdown.Token);
            await WaitUntilAsync(() => { lock (outputGate) return output.ToString().Contains("40 100", StringComparison.Ordinal); }, shutdown.Token);
            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("cd /tmp\n"), shutdown.Token);
            await WaitUntilAsync(() => observedCwd == "/tmp", shutdown.Token);

            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("exit\n"), shutdown.Token);
            Assert.Equal(0, await shell.WaitForExitAsync(shutdown.Token));
            await pump.WaitAsync(shutdown.Token);
        }
        finally
        {
            if (!shell.HasExited) shell.KillTree();
            try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPtyStopEscalatesForShellThatIgnoresHangupAndTerm()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hitch stubborn shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shell = LinuxPtySession.Start("/bin/bash", "--noprofile -i", root, 80, 24,
            Path.Combine(root, "resize.sock"), "/bin/true");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await WaitUntilAsync(() => ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("bash"), timeout.Token);
            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("trap '' HUP TERM; while :; do sleep 1; done\n"), timeout.Token);
            await WaitUntilAsync(() => ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("sleep"), timeout.Token);
            var started = Stopwatch.GetTimestamp();
            shell.KillTree();
            await shell.WaitForExitAsync(timeout.Token);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!shell.HasExited) shell.KillTree();
            await shell.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPtyStdinEofAlsoEscalatesForShellThatIgnoresHangup()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hitch eof shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shell = LinuxPtySession.Start("/bin/bash", "--noprofile -i", root, 80, 24,
            Path.Combine(root, "resize.sock"), "/bin/true");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await WaitUntilAsync(() => ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("bash"), timeout.Token);
            await shell.WriteInputAsync(Encoding.UTF8.GetBytes("trap '' HUP TERM; while :; do sleep 1; done\n"), timeout.Token);
            await WaitUntilAsync(() => ProcessTreeInspector.GetDescendantNames(shell.ProcessId).Contains("sleep"), timeout.Token);
            var started = Stopwatch.GetTimestamp();
            shell.CloseInput();
            await shell.WaitForExitAsync(timeout.Token);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!shell.HasExited) shell.KillTree();
            await shell.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxHostRestoresTerminalModeAfterSessionStop()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runtime = Path.Combine(Path.GetTempPath(), "hitch terminal test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtime);
        var hitch = FindBuiltHitch();
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(hitch)!, "hitch.dll")), "The Hitch apphost must remain beside its managed files.");
        var command = $"stty -g; {QuoteShell(hitch)} on --shell /bin/bash; stty -g";
        var script = new ProcessStartInfo("script")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        script.ArgumentList.Add("-qefc");
        script.ArgumentList.Add(command);
        script.ArgumentList.Add("/dev/null");
        script.Environment["XDG_RUNTIME_DIR"] = runtime;
        using var terminal = Process.Start(script) ?? throw new InvalidOperationException("Could not start script(1) for terminal mode test.");
        var output = new StringBuilder();
        var reader = ReadOutputAsync(terminal.StandardOutput.BaseStream, output);
        try
        {
            await WaitUntilAsync(() => { lock (output) return output.ToString().Contains("Hitch session", StringComparison.Ordinal); },
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            string sessionText;
            lock (output) sessionText = output.ToString();
            var match = Regex.Match(sessionText, @"Hitch session (\d+)");
            Assert.True(match.Success, sessionText);
            await WaitUntilAsync(() =>
            {
                lock (output)
                {
                    var text = output.ToString();
                    return text.Contains("#" + match.Groups[1].Value, StringComparison.Ordinal)
                           && text.Contains("\u001b]7;file://localhost", StringComparison.Ordinal);
                }
            }, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            var stopInfo = new ProcessStartInfo(hitch) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            stopInfo.ArgumentList.Add("kill");
            stopInfo.ArgumentList.Add(match.Groups[1].Value);
            stopInfo.Environment["XDG_RUNTIME_DIR"] = runtime;
            using var stopper = Process.Start(stopInfo) ?? throw new InvalidOperationException("Could not stop terminal test session.");
            await stopper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, stopper.ExitCode);
            await terminal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await reader.WaitAsync(TimeSpan.FromSeconds(2));
            string completeOutput;
            lock (output) completeOutput = output.ToString();
            var modes = Regex.Matches(completeOutput, @"(?m)^([0-9a-fA-F]+(?::[0-9a-fA-F]+)+)\r?$")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.True(modes.Length >= 2, completeOutput);
            Assert.Equal(modes[0], modes[^1]);
        }
        finally
        {
            if (!terminal.HasExited) terminal.Kill(entireProcessTree: true);
            try { await reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            if (Directory.Exists(runtime)) Directory.Delete(runtime, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(50, cancellationToken);
    }

    private static async Task ReadOutputAsync(Stream stream, StringBuilder output)
    {
        var buffer = new byte[2048];
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) return;
            lock (output) output.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }
    }

    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string FindBuiltHitch()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Hitch", "bin", BuildConfiguration, "net10.0", "linux-x64", "hitch");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate the Linux Hitch apphost in its project output directory.");
    }
}
