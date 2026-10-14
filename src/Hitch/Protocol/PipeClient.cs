using System.IO.Pipes;
using System.Net.Sockets;

namespace Hitch.Protocol;

public static class PipeClient
{
    public static async Task<PipeResponse> SendAsync(string pipeName, PipeRequest request, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(pipeName), cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await FramedJson.WriteAsync(stream, request, cancellationToken);
            return await FramedJson.ReadAsync<PipeResponse>(stream, cancellationToken)
                ?? new PipeResponse(false, "Session host closed the connection without responding.");
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Hitch session control is supported on Windows and Linux only.");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, cancellationToken);
        await FramedJson.WriteAsync(pipe, request, cancellationToken);
        return await FramedJson.ReadAsync<PipeResponse>(pipe, cancellationToken)
            ?? new PipeResponse(false, "Session host closed the connection without responding.");
    }
}
