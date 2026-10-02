namespace Hitch.Sessions;

public sealed class BoundedOutputLog
{
    public const long MaxBytes = 32 * 1024 * 1024;
    public const long RetainBytes = 28 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly long _retainBytes;

    public BoundedOutputLog(string path, long maxBytes = MaxBytes, long retainBytes = RetainBytes)
    {
        if (maxBytes <= 0 || retainBytes <= 0 || retainBytes > maxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _path = path;
        _maxBytes = maxBytes;
        _retainBytes = retainBytes;
    }

    public async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                await stream.WriteAsync(bytes, cancellationToken);
            var info = new FileInfo(_path);
            if (info.Length <= _maxBytes)
                return;

            await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            input.Seek(-Math.Min(_retainBytes, input.Length), SeekOrigin.End);
            var retained = new byte[checked((int)Math.Min(_retainBytes, input.Length))];
            await input.ReadExactlyAsync(retained, cancellationToken);
            var firstNewline = Array.IndexOf(retained, (byte)'\n');
            var offset = firstNewline >= 0 ? firstNewline + 1 : 0;
            await File.WriteAllBytesAsync(_path, retained[offset..], cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return string.Empty;
            var bytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }
}
