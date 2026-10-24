using System.Text.RegularExpressions;
using Mailnix.Core.Messages;

namespace Mailnix.Core.Rules;

public enum NamedListKind
{
    /// <summary>Exact addresses, e.g. <c>boss@example.com</c>.</summary>
    Address,

    /// <summary>Domains including their subdomains, e.g. <c>example.com</c>.</summary>
    Domain,

    /// <summary>Wildcard patterns on addresses, e.g. <c>*@*.shop.example</c> or <c>noreply@*</c>.</summary>
    Pattern,
}

/// <summary>A reusable list of addresses, domains or patterns (e.g. "VIP", "Blocked domains").</summary>
public sealed partial class NamedList
{
    private readonly HashSet<string> _values;
    private readonly Regex[] _patterns;

    public NamedList(Guid id, string name, NamedListKind kind, IEnumerable<string> entries)
    {
        Id = id;
        Name = name;
        Kind = kind;
        _values = [.. entries.Select(e => Normalize(kind, e)).Where(e => e.Length > 0)];
        _patterns = kind == NamedListKind.Pattern ? [.. _values.Select(ToRegex)] : [];
    }

    public Guid Id { get; }

    public string Name { get; }

    public NamedListKind Kind { get; }

    public int Count => _values.Count;

    /// <summary>The normalized entries (see <see cref="Normalize"/>).</summary>
    public IReadOnlyCollection<string> Entries => _values;

    /// <summary>The form entries are stored and compared in (lowercase; domains as punycode).</summary>
    public static string Normalize(NamedListKind kind, string entry)
    {
        var trimmed = entry.Trim();
        return kind switch
        {
            NamedListKind.Address => trimmed.Length == 0 ? string.Empty : new EmailAddress(trimmed).Address,
            NamedListKind.Domain => EmailAddress.NormalizeDomain(trimmed.TrimStart('@', '.', '*')),
            _ => trimmed.ToLowerInvariant(),
        };
    }

    /// <summary>
    /// Human-readable form of a normalized entry: internationalized domains as Unicode ("müller.de" instead of
    /// "xn--mller-kva.de"). Normalizing it again gives the stored value back.
    /// </summary>
    public static string ToDisplay(NamedListKind kind, string normalized)
    {
        string Unicode(string domain)
        {
            try
            {
                return Idn.GetUnicode(domain);
            }
            catch (ArgumentException)
            {
                return domain;
            }
        }

        return kind switch
        {
            NamedListKind.Domain => Unicode(normalized),
            NamedListKind.Address when normalized.LastIndexOf('@') is var at and > 0 => normalized[..(at + 1)] + Unicode(normalized[(at + 1)..]),
            _ => normalized,
        };
    }

    private static readonly System.Globalization.IdnMapping Idn = new();

    /// <summary>Why an entry is not valid for this kind of list, or null if it is.</summary>
    public static string? ValidateEntry(NamedListKind kind, string entry)
    {
        var value = entry.Trim();
        if (value.Length == 0)
        {
            return "Entry is empty.";
        }

        if (value.Length > MaxEntryLength)
        {
            return $"Entries can be at most {MaxEntryLength} characters.";
        }

        return kind switch
        {
            NamedListKind.Address when !AddressPattern().IsMatch(Normalize(kind, value)) => "Not an email address.",
            NamedListKind.Domain when !DomainPattern().IsMatch(Normalize(kind, value)) => "Not a domain name.",
            NamedListKind.Pattern when value.All(c => c is '*' or '?' or '@' or '.') => "A pattern needs at least one letter or digit.",
            NamedListKind.Pattern when value.Any(char.IsWhiteSpace) => "Patterns cannot contain spaces.",
            _ => null,
        };
    }

    public const int MaxEntryLength = 320;

    public bool ContainsAddress(EmailAddress address) => Kind switch
    {
        NamedListKind.Address => _values.Contains(address.Address) || _values.Contains(address.BaseAddress),
        NamedListKind.Domain => ContainsDomain(address.Domain),
        _ => _patterns.Any(p => p.IsMatch(address.Address)),
    };

    public bool ContainsDomain(string domain)
    {
        var normalized = EmailAddress.NormalizeDomain(domain);
        return Kind switch
        {
            NamedListKind.Domain => _values.Any(v => EmailAddress.IsDomainOrSubdomain(normalized, v)),
            NamedListKind.Pattern => _patterns.Any(p => p.IsMatch(normalized)),
            _ => false,
        };
    }

    private static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    [GeneratedRegex(@"^[^@\s]+@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9-]{2,63}$")]
    private static partial Regex DomainPattern();
}
