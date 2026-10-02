namespace Hitch.Sessions;

public sealed class BoundedOutputLog : IAsyncDisposable
{
    public const long MaxBytes = 32 * 1024 * 1024;
    public const long RetainBytes = 28 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly long _retainBytes;
    private FileStream? _stream;
    private bool _disposed;

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
            var stream = GetWriter();
            stream.Position = stream.Length;
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            if (stream.Length <= _maxBytes)
                return;

            stream.Position = Math.Max(0, stream.Length - Math.Min(_retainBytes, stream.Length));
            var retained = new byte[checked((int)Math.Min(_retainBytes, stream.Length))];
            await stream.ReadExactlyAsync(retained, cancellationToken);
            var firstNewline = Array.IndexOf(retained, (byte)'\n');
            var offset = firstNewline >= 0 ? firstNewline + 1 : 0;
            stream.SetLength(0);
            stream.Position = 0;
            await stream.WriteAsync(retained.AsMemory(offset), cancellationToken);
            await stream.FlushAsync(cancellationToken);
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is null)
                return File.Exists(_path)
                    ? System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_path, cancellationToken))
                    : string.Empty;

            var position = _stream.Position;
            var bytes = new byte[checked((int)_stream.Length)];
            try
            {
                _stream.Position = 0;
                await _stream.ReadExactlyAsync(bytes, cancellationToken);
            }
            finally
            {
                _stream.Position = position;
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_stream is not null)
            {
                await _stream.FlushAsync();
                await _stream.DisposeAsync();
                _stream = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private FileStream GetWriter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null)
            return _stream;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.Asynchronous);
        _stream.Position = _stream.Length;
        return _stream;
    }
}
