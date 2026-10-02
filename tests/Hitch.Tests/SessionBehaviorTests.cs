using System.Diagnostics;
using System.Text;
using Hitch.Sessions;
using Hitch.Native;
using Xunit;

namespace Hitch.Tests;

public sealed class SessionBehaviorTests
{
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
}
