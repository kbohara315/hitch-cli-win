using System.Text;

namespace Hitch.Sessions;

public sealed class CwdMarkerParser
{
    private const string Prefix = "\u001b]7;file://localhost/";
    private readonly StringBuilder _pending = new();

    public string? Observe(ReadOnlySpan<byte> bytes)
    {
        _pending.Append(Encoding.UTF8.GetString(bytes));
        var text = _pending.ToString();
        var marker = text.LastIndexOf(Prefix, StringComparison.Ordinal);
        if (marker < 0)
        {
            if (_pending.Length > 2048)
                _pending.Remove(0, _pending.Length - Prefix.Length);
            return null;
        }

        var start = marker + Prefix.Length;
        var end = text.IndexOfAny(['\a', '\u001b'], start);
        if (end < 0)
        {
            if (_pending.Length > 4096)
                _pending.Remove(0, marker);
            return null;
        }

        var encodedPath = text[start..end];
        _pending.Clear();
        if (end + 1 < text.Length && text[end] == '\u001b' && text[end + 1] == '\\')
            _pending.Append(text.AsSpan(end + 2));
        else
            _pending.Append(text.AsSpan(end + 1));

        try
        {
            var path = Uri.UnescapeDataString(encodedPath).Replace('/', Path.DirectorySeparatorChar);
            if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == Path.DirectorySeparatorChar && path[2] == ':')
                path = path[1..];
            return Path.IsPathFullyQualified(path) && Directory.Exists(path) ? path : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
