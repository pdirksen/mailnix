using System.Globalization;
using System.Text;
using Mailnix.Core.Messages;

namespace Mailnix.Core.Rules;

/// <summary>
/// Target folders with placeholders, e.g. <c>INBOX/Archive/{year}/{month}</c> or <c>Shops/{fromDomain}</c>.
/// "/" separates levels and becomes the server's delimiter; placeholder values are sanitized so they can
/// never create extra levels (a domain like "shop.de" stays one folder on servers that use "." as delimiter).
/// </summary>
public static class FolderTemplate
{
    public const int MaxLength = 500;

    public static readonly IReadOnlyList<string> Placeholders =
        ["year", "month", "day", "fromDomain", "fromAddress", "fromName", "listId", "plusTag"];

    /// <summary>Returns problems with the template; empty when it is valid.</summary>
    public static IReadOnlyList<string> Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return ["Folder is required."];
        }

        if (template.Length > MaxLength)
        {
            return [$"Folder must be at most {MaxLength} characters."];
        }

        var errors = new List<string>();
        if (!TryParse(template, out var parts, out var parseError))
        {
            errors.Add(parseError!);
        }
        else
        {
            errors.AddRange(parts.Where(p => p.IsPlaceholder && !Placeholders.Contains(p.Text, StringComparer.Ordinal))
                .Select(p => $"Unknown placeholder {{{p.Text}}}. Available: {string.Join(", ", Placeholders.Select(n => $"{{{n}}}"))}."));
        }

        if (template.Split('/').Any(segment => segment.Trim().Length == 0))
        {
            errors.Add("Folder levels must not be empty (check for \"//\" or a leading/trailing \"/\").");
        }

        return errors;
    }

    /// <summary>The server folder path for this message. Assumes a valid template.</summary>
    public static string Resolve(string template, MessageFacts facts, EvaluationContext context)
    {
        var delimiter = context.FolderDelimiter;
        var date = TimeZoneInfo.ConvertTime(facts.Message.EffectiveDate ?? context.Now, context.TimeZone);
        string Value(string name) => name switch
        {
            "year" => date.Year.ToString("0000", CultureInfo.InvariantCulture),
            "month" => date.Month.ToString("00", CultureInfo.InvariantCulture),
            "day" => date.Day.ToString("00", CultureInfo.InvariantCulture),
            "fromDomain" => facts.Message.From?.Domain ?? string.Empty,
            "fromAddress" => facts.Message.From?.Address ?? string.Empty,
            "fromName" => facts.Message.From?.Name ?? facts.Message.From?.Address ?? string.Empty,
            "listId" => facts.ListId ?? string.Empty,
            "plusTag" => facts.PlusTags is [var tag, ..] ? tag : string.Empty,
            _ => string.Empty,
        };

        var segments = template.Split('/').Select(segment =>
        {
            // Invalid templates are rejected on save; should one slip through, use the text literally.
            return TryParse(segment, out var parts, out _)
                ? string.Concat(parts.Select(p => p.IsPlaceholder ? Sanitize(Value(p.Text), delimiter) : p.Text)).Trim()
                : segment.Trim();
        });
        return string.Join(delimiter, segments);
    }

    /// <summary>
    /// The server path of the levels before the first placeholder, e.g. <c>INBOX.Archive</c> for
    /// <c>INBOX/Archive/{year}</c>; null when the first level already has one.
    /// </summary>
    public static string? StaticPrefix(string template, char delimiter)
    {
        var segments = template.Split('/').Select(s => s.Trim()).TakeWhile(s => !s.Contains('{', StringComparison.Ordinal)).ToList();
        return segments.Count == 0 ? null : string.Join(delimiter, segments);
    }

    /// <summary>Makes a value safe as part of one folder name.</summary>
    internal static string Sanitize(string value, char delimiter)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            builder.Append(c == delimiter || c is '/' or '\\' or '*' or '%' || char.IsControl(c) ? '_' : c);
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    private readonly record struct Part(string Text, bool IsPlaceholder);

    private static bool TryParse(string template, out List<Part> parts, out string? error)
    {
        parts = [];
        error = null;
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            switch (template[i])
            {
                case '{':
                    var end = template.IndexOf('}', i + 1);
                    var nested = template.IndexOf('{', i + 1);
                    if (end < 0 || (nested >= 0 && nested < end))
                    {
                        error = "Unclosed \"{\" in folder.";
                        return false;
                    }

                    if (literal.Length > 0)
                    {
                        parts.Add(new Part(literal.ToString(), false));
                        literal.Clear();
                    }

                    parts.Add(new Part(template[(i + 1)..end].Trim(), true));
                    i = end;
                    break;
                case '}':
                    error = "Unexpected \"}\" in folder.";
                    return false;
                default:
                    literal.Append(template[i]);
                    break;
            }
        }

        if (literal.Length > 0)
        {
            parts.Add(new Part(literal.ToString(), false));
        }

        return true;
    }
}
