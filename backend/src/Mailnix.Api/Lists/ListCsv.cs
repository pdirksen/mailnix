using System.Text;

namespace Mailnix.Api.Lists;

/// <summary>Two-column CSV (value, note) for list import/export. Accepts "," or ";" and quoted fields.</summary>
internal static class ListCsv
{
    public static IEnumerable<(int Line, string Value, string? Note)> Parse(string csv)
    {
        var lineNumber = 0;
        foreach (var raw in csv.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var fields = SplitFields(line);
            var value = fields[0].Trim();
            if (lineNumber == 1 && value.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                continue; // header row
            }

            var note = fields.Count > 1 && !string.IsNullOrWhiteSpace(fields[1]) ? fields[1].Trim() : null;
            yield return (lineNumber, value, note);
        }
    }

    public static string Write(IEnumerable<(string Value, string? Note)> entries)
    {
        var builder = new StringBuilder("value,note\r\n");
        foreach (var (value, note) in entries)
        {
            builder.Append(Quote(value)).Append(',').Append(Quote(Defuse(note ?? string.Empty))).Append("\r\n");
        }

        return builder.ToString();
    }

    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c is ',' or ';')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private static string Quote(string value) =>
        value.IndexOfAny([',', ';', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    // Spreadsheet apps execute cells starting with these characters as formulas (CSV injection).
    private static string Defuse(string text) => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' ? "'" + text : text;
}
