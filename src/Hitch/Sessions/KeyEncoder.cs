using System.Text;

namespace Hitch.Sessions;

public static class KeyEncoder
{
    public static byte[] Encode(IReadOnlyList<string> keys)
    {
        var result = new List<byte>();
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            if (index > 0 && !IsNamedKey(key) && !IsNamedKey(keys[index - 1]))
                result.Add((byte)' ');
            var bytes = key switch
            {
                "Enter" or "C-m" => [(byte)'\r'],
                "Tab" => [(byte)'\t'],
                "Escape" or "Esc" => [(byte)0x1b],
                "Space" => [(byte)' '],
                "Backspace" or "BSpace" => [(byte)0x7f],
                _ when key.StartsWith("C-", StringComparison.OrdinalIgnoreCase) && key.Length == 3
                    => [(byte)(char.ToUpperInvariant(key[2]) & 0x1f)],
                _ => Encoding.UTF8.GetBytes(key)
            };
            result.AddRange(bytes);
        }
        return result.ToArray();
    }

    public static bool IsControlOnly(IReadOnlyList<string> keys)
    {
        return keys.Count > 0 && keys.All(key => key.StartsWith("C-", StringComparison.OrdinalIgnoreCase) && key.Length == 3);
    }

    public static bool StartsWithInterrupt(IReadOnlyList<string> keys) =>
        keys.Count > 0 && keys[0].Equals("C-c", StringComparison.OrdinalIgnoreCase);

    private static bool IsNamedKey(string key) => key is "Enter" or "C-m" or "Tab" or "Escape" or "Esc"
        or "Space" or "Backspace" or "BSpace" || key.StartsWith("C-", StringComparison.OrdinalIgnoreCase) && key.Length == 3;
}
