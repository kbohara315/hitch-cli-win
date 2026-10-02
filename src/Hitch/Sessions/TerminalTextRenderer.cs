using System.Text.RegularExpressions;

namespace Hitch.Sessions;

public static partial class TerminalTextRenderer
{
    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))")]
    private static partial Regex EscapeSequences();

    public static string Render(string text)
    {
        var withoutEscapes = EscapeSequences().Replace(text, string.Empty);
        var rows = new List<System.Text.StringBuilder> { new() };
        var row = 0;
        var column = 0;
        foreach (var character in withoutEscapes)
        {
            switch (character)
            {
                case '\r':
                    column = 0;
                    continue;
                case '\n':
                    row++;
                    column = 0;
                    while (rows.Count <= row)
                        rows.Add(new System.Text.StringBuilder());
                    continue;
                case '\b':
                    column = Math.Max(0, column - 1);
                    continue;
                case '\t':
                    var spaces = 8 - column % 8;
                    for (var index = 0; index < spaces; index++)
                        Put(' ');
                    continue;
                default:
                    if (character < ' ')
                        continue;
                    Put(character);
                    break;
            }
        }
        return string.Join('\n', rows.Select(line => line.ToString().TrimEnd()));

        void Put(char value)
        {
            var line = rows[row];
            while (line.Length < column)
                line.Append(' ');
            if (line.Length == column)
                line.Append(value);
            else
                line[column] = value;
            column++;
        }
    }

    public static string Tail(string text, int limit)
    {
        var lines = Render(text).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(Math.Clamp(limit, 1, 1000)));
    }
}
