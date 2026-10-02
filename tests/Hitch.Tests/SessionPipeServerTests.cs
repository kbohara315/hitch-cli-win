using System.Diagnostics;
using Hitch.Native;
using Hitch.Protocol;
using Hitch.Sessions;
using Xunit;

namespace Hitch.Tests;

public sealed class SessionPipeServerTests
{
    [Fact]
    public async Task SameUserAgentCommandsUsePipeAndGuardOrdinaryInputWhileBusy()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-pipe-test-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(root);
        var id = "pipe-session";
        var pipeName = "hitch-test-" + Guid.NewGuid().ToString("N");
        var fakeShell = new FakeConPtySession();
        var processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        store.Save(new SessionRecord(id, Environment.ProcessId, "cmd.exe", root, pipeName,
            new DateTimeOffset(processStart)));
        var activity = new SessionActivityTracker(new SessionState(true, "node", root, DateTimeOffset.UtcNow, AtPrompt: false));
        store.SaveState(id, activity.Current);

        using var shutdown = new CancellationTokenSource();
        await using var outputLog = new BoundedOutputLog(store.OutputPath(id));
        await using var activeOutputLog = new BoundedOutputLog(store.ActiveOutputPath(id));
        var server = new SessionPipeServer(pipeName, id, store, fakeShell,
            outputLog, activeOutputLog,
            activity, shutdown.Cancel);
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            var refused = await PipeClient.SendAsync(pipeName,
                new PipeRequest("send-keys", ["typing"]));
            Assert.False(refused.Ok);
            Assert.Contains("--force", refused.Message, StringComparison.OrdinalIgnoreCase);

            var interrupt = await PipeClient.SendAsync(pipeName,
                new PipeRequest("send-keys", ["C-c"]));
            Assert.True(interrupt.Ok);
            Assert.Equal(new byte[] { 0x03 }, fakeShell.Input);

            store.SaveState(id, activity.PromptObserved(root, DateTimeOffset.UtcNow));
            var command = await PipeClient.SendAsync(pipeName,
                new PipeRequest("send-keys", ["Write-Output ready", "Enter"]));
            Assert.True(command.Ok);
            Assert.Equal("Write-Output ready\r", System.Text.Encoding.UTF8.GetString(fakeShell.Input));

            var stop = await PipeClient.SendAsync(pipeName, new PipeRequest("stop"));
            Assert.True(stop.Ok);
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            shutdown.Cancel();
            store.Delete(id);
            await fakeShell.DisposeAsync();
        }
    }

    private sealed class FakeConPtySession : IConPtySession
    {
        public int ProcessId => Environment.ProcessId;
        public bool HasExited => false;
        public byte[] Input { get; private set; } = [];
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Input = bytes.ToArray();
            return Task.CompletedTask;
        }
        public Task PumpOutputAsync(Func<ReadOnlyMemory<byte>, Task> onOutput, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Resize(short columns, short rows) { }
        public void KillTree() { }
        public void ClosePseudoConsole() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
