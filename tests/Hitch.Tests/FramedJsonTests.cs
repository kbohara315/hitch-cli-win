using Hitch.Protocol;
using Xunit;

namespace Hitch.Tests;

public sealed class FramedJsonTests
{
    [Fact]
    public async Task RoundTripsJsonAcrossPartialReads()
    {
        await using var stream = new FragmentingStream();
        var sent = new PipeRequest("send-keys", ["Write-Output hello", "Enter"], Force: true);

        await FramedJson.WriteAsync(stream, sent);
        stream.Position = 0;
        var received = await FramedJson.ReadAsync<PipeRequest>(stream);

        Assert.NotNull(received);
        Assert.Equal(sent.Action, received.Action);
        Assert.Equal(sent.Keys, received.Keys);
        Assert.Equal(sent.Force, received.Force);
    }

    [Fact]
    public async Task RejectsAnInvalidFrameLength()
    {
        await using var stream = new MemoryStream(BitConverter.GetBytes(-1));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FramedJson.ReadAsync<PipeRequest>(stream));
    }

    [Fact]
    public async Task ReadsWebDefaultNumberValuesEncodedAsStrings()
    {
        await using var stream = new MemoryStream();
        var payload = System.Text.Encoding.UTF8.GetBytes("{\"action\":\"context\",\"tail\":\"42\"}");
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
        await stream.WriteAsync(payload);
        stream.Position = 0;

        var received = await FramedJson.ReadAsync<PipeRequest>(stream);

        Assert.NotNull(received);
        Assert.Equal(42, received.Tail);
    }

    private sealed class FragmentingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}
