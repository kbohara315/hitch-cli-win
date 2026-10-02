using System.IO.Pipes;

namespace Hitch.Protocol;

public static class PipeClient
{
    public static async Task<PipeResponse> SendAsync(string pipeName, PipeRequest request, CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, cancellationToken);
        await FramedJson.WriteAsync(pipe, request, cancellationToken);
        return await FramedJson.ReadAsync<PipeResponse>(pipe, cancellationToken)
            ?? new PipeResponse(false, "Session host closed the connection without responding.");
    }
}
