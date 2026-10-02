using System.Buffers.Binary;
using System.Text.Json;

namespace Hitch.Protocol;

public static class FramedJson
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (payload.Length > MaxMessageBytes)
            throw new InvalidDataException("IPC message exceeds the 1 MiB limit.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken))
            return default;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxMessageBytes)
            throw new InvalidDataException($"Invalid IPC message length: {length}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, Options);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var amount = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (amount == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("IPC message ended inside its header.");
            }
            read += amount;
        }
        return true;
    }
}
