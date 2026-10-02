using System.Diagnostics;
using System.Text;
using Hitch.Sessions;
using Hitch.Native;
using Hitch;
using Xunit;

namespace Hitch.Tests;

public sealed class SessionBehaviorTests
{
    [Fact]
    public void SessionActivityTransitionsKeepPromptAndForegroundStateConsistent()
    {
        var now = DateTimeOffset.UtcNow;
        var initial = new SessionState(false, null, "C:\\work", now, true);

        var submitted = SessionActivity.Submitted(initial, now.AddSeconds(1));
        Assert.True(submitted.CommandRunning);
        Assert.False(submitted.AtPrompt);
        Assert.Equal("shell command", submitted.ActiveCommand);

        var process = SessionActivity.ProcessObserved(submitted, "dotnet", now.AddSeconds(2));
        Assert.Equal("dotnet", process.ActiveCommand);
        Assert.True(process.CommandRunning);

        var prompt = SessionActivity.PromptObserved(process, "C:\\next", now.AddSeconds(3));
        Assert.False(prompt.CommandRunning);
        Assert.True(prompt.AtPrompt);
        Assert.Null(prompt.ActiveCommand);
        Assert.Equal("C:\\next", prompt.CurrentDirectory);
    }

    [Fact]
    public async Task SessionLifecycleCoordinatesStopAndCleansUpInOrderWithoutNativeShell()
    {
        var order = new List<string>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = SessionLifecycle.RunAsync(exit.Task, input.Task, stop.Task,
            () => { order.Add("kill"); exit.TrySetResult(143); },
            () => { order.Add("close"); output.TrySetResult(); }, output.Task,
            () => { order.Add("cancel"); background.TrySetResult(); }, [background.Task],
            () => { order.Add("dispose"); return ValueTask.CompletedTask; });

        stop.SetResult();
        Assert.Equal(0, await lifecycle);
        Assert.Equal(["kill", "close", "cancel", "dispose"], order);
    }

    [Theory]
    [InlineData(new string[0], "Start", null, null)]
    [InlineData(new[] { "START", "--shell", "pwsh.exe" }, "Start", "pwsh.exe", "START")]
    [InlineData(new[] { "on", "--shell", "cmd.exe" }, "Start", "cmd.exe", "on")]
    [InlineData(new[] { "--help" }, "Help", null, "--help")]
    [InlineData(new[] { "status", "12" }, "Status", null, "status")]
    public void ProgramOwnsTopLevelAliasesAndStartOptions(string[] args, string kind, string? shell, string? command)
    {
        var parsed = Program.Parse(args);

        Assert.Equal(kind, parsed.Kind.ToString());
        Assert.Equal(shell, parsed.ShellPath);
        Assert.Equal(command, parsed.Command);
        Assert.Equal(args.Skip(1), parsed.Arguments);
    }

    [Theory]
    [InlineData(0u, 0x0du)]
    [InlineData(0x12u, 0x1fu)]
    [InlineData(0x0du, 0x0du)]
    public void VirtualTerminalOutputModePreservesExistingFlagsAndEnablesTerminalWrapSemantics(
        uint original,
        uint expected)
    {
        Assert.Equal(expected, ConsoleModeScope.GetVirtualTerminalOutputMode(original));
    }

    [Fact]
    public void EncodesNamedKeysAndPreservesTextSpaces()
    {
        var bytes = KeyEncoder.Encode(["Write-Output hello", "Enter"]);

        Assert.Equal("Write-Output hello\r", Encoding.UTF8.GetString(bytes));
        Assert.Equal("echo one two", Encoding.UTF8.GetString(KeyEncoder.Encode(["echo", "one", "two"])));
    }

    [Fact]
    public void AllowsControlKeysButRecognizesOrdinaryTextAsInput()
    {
        Assert.True(KeyEncoder.IsControlOnly(["C-c", "C-l"]));
        Assert.True(KeyEncoder.StartsWithInterrupt(["C-c", "Enter"]));
        Assert.False(KeyEncoder.IsControlOnly(["C-c", "hello"]));
    }

    [Fact]
    public void RendersTerminalEscapesAndCarriageReturnUpdatesAsPlainText()
    {
        var rendered = TerminalTextRenderer.Render("\u001b[32mready\u001b[0m\rprogress\n");

        Assert.Equal("progress\n", rendered);
    }

    [Fact]
    public void ExtractsAnOsc7WorkingDirectoryMarker()
    {
        var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var encoded = Uri.EscapeDataString(directory);
        var marker = Encoding.UTF8.GetBytes($"\u001b]7;file://localhost/{encoded}\u0007");
        var parser = new CwdMarkerParser();

        Assert.Equal(directory, parser.Observe(marker));
    }

    [Fact]
    public void SessionStorePersistsStateAndListsItsLiveHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-tests-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        var processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var record = new SessionRecord("session-a", Environment.ProcessId, "pwsh.exe", root, "hitch-session-a",
            new DateTimeOffset(processStart));
        try
        {
            store.Save(record);
            store.SaveState(record.Id, new SessionState(true, "dotnet", root, DateTimeOffset.UtcNow));

            Assert.Single(store.ListLive());
            Assert.Equal("dotnet", store.ReadState(record.Id).ActiveCommand);
            Assert.Equal(record, store.Find(record.Id));
        }
        finally
        {
            store.Delete(record.Id);
        }
    }

    [Fact]
    public void SessionStoreAllocatesDistinctShortIdsForConcurrentSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-ids-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        try
        {
            Assert.Equal("1", store.AllocateId());
            Assert.Equal("2", store.AllocateId());
        }
        finally
        {
            store.Delete("1");
            store.Delete("2");
        }
    }

    [Fact]
    public async Task SessionStoreAllocatesDistinctIdsAcrossConcurrentCallers()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-ids-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        try
        {
            var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(store.AllocateId)));
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionStorePrunesOldCorruptRecordsButPreservesRecentIncompleteStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-stale-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        var old = store.SessionDirectory("old");
        var recent = store.SessionDirectory("recent");
        try
        {
            Directory.CreateDirectory(old);
            File.WriteAllText(store.RecordPath("old"), "{");
            File.SetLastWriteTimeUtc(store.RecordPath("old"), DateTime.UtcNow.AddMinutes(-1));
            Directory.CreateDirectory(recent);
            File.WriteAllText(store.RecordPath("recent"), "{");

            Assert.Empty(store.ListLive());
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(recent));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionStoreUsesProcessIdentityAndStartTimeToRejectStaleRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-liveness-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var stale = new SessionRecord("1", Environment.ProcessId, "pwsh.exe", root, "hitch-1",
            new DateTimeOffset(started.AddMinutes(-1)));
        try
        {
            store.Save(stale);
            Assert.Empty(store.ListLive());
            Assert.False(Directory.Exists(store.SessionDirectory(stale.Id)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionStoreReservesIdsForRecentIncompleteStartupDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-reserve-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        try
        {
            Directory.CreateDirectory(store.SessionDirectory("1"));

            Assert.Equal("2", store.AllocateId());
            Assert.True(Directory.Exists(store.SessionDirectory("1")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionStoreGracePeriodBoundaryIsStrictlyOlderThanFiveSeconds()
    {
        var now = DateTime.UtcNow;

        Assert.False(SessionStore.IsPastStartupGracePeriod(now - SessionStore.IncompleteRecordGracePeriod, now));
        Assert.True(SessionStore.IsPastStartupGracePeriod(now - SessionStore.IncompleteRecordGracePeriod - TimeSpan.FromTicks(1), now));
    }
}
