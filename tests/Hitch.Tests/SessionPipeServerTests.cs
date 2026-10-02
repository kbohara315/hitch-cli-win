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
        store.SaveState(id, new SessionState(true, "node", root, DateTimeOffset.UtcNow, AtPrompt: false));

        using var shutdown = new CancellationTokenSource();
        var server = new SessionPipeServer(pipeName, id, store, fakeShell,
            new BoundedOutputLog(store.OutputPath(id)), new BoundedOutputLog(store.ActiveOutputPath(id)),
            shutdown.Cancel);
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

            store.SaveState(id, new SessionState(false, null, root, DateTimeOffset.UtcNow, AtPrompt: true));
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
