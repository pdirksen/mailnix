using System.Globalization;

namespace Mailnix.Core.Rules;

public enum SearchTextField
{
    From,
    To,
    Cc,
    Subject,

    /// <summary>Any header field named <see cref="SearchText.HeaderName"/>.</summary>
    Header,
}

public enum SearchFlagKind
{
    Seen,
    Flagged,
    Answered,
}

/// <summary>
/// Criteria the mail server evaluates (IMAP SEARCH), built by <see cref="SearchPlanner"/>. Text criteria are
/// case-insensitive substring matches on the whole header field; dates are the server's arrival date (INTERNALDATE).
/// </summary>
public abstract record SearchFilter
{
    /// <summary>IMAP-like text for logs and the activity view, e.g. <c>FROM "shop" BEFORE 2026-09-24</c>.</summary>
    public abstract string Describe();

    public override string ToString() => Describe();

    protected static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    protected static string Group(SearchFilter filter) => filter is SearchAnd or SearchOr ? $"({filter.Describe()})" : filter.Describe();
}

public sealed record SearchAnd(IReadOnlyList<SearchFilter> Children) : SearchFilter
{
    public override string Describe() => string.Join(" ", Children.Select(c => c is SearchOr ? Group(c) : c.Describe()));
}

public sealed record SearchOr(IReadOnlyList<SearchFilter> Children) : SearchFilter
{
    public override string Describe() => string.Join(" OR ", Children.Select(Group));
}

public sealed record SearchNot(SearchFilter Inner) : SearchFilter
{
    public override string Describe() => $"NOT {Group(Inner)}";
}

/// <param name="Value">Empty with <see cref="SearchTextField.Header"/>: every message that has the header.</param>
public sealed record SearchText(SearchTextField Field, string Value, string? HeaderName = null) : SearchFilter
{
    public override string Describe() => Field == SearchTextField.Header
        ? $"HEADER {HeaderName} {Quote(Value)}"
        : $"{Field.ToString().ToUpperInvariant()} {Quote(Value)}";
}

public sealed record SearchFlagIs(SearchFlagKind Flag, bool Set) : SearchFilter
{
    public override string Describe() => (Set ? string.Empty : "UN") + Flag.ToString().ToUpperInvariant();
}

public sealed record SearchKeyword(string Keyword) : SearchFilter
{
    public override string Describe() => $"KEYWORD {Keyword}";
}

/// <summary>Larger than this many bytes.</summary>
public sealed record SearchLarger(long Bytes) : SearchFilter
{
    public override string Describe() => string.Create(CultureInfo.InvariantCulture, $"LARGER {Bytes}");
}

/// <summary>Smaller than this many bytes.</summary>
public sealed record SearchSmaller(long Bytes) : SearchFilter
{
    public override string Describe() => string.Create(CultureInfo.InvariantCulture, $"SMALLER {Bytes}");
}

/// <summary>Arrived before this day.</summary>
public sealed record SearchBefore(DateOnly Day) : SearchFilter
{
    public override string Describe() => $"BEFORE {Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

/// <summary>Arrived on or after this day.</summary>
public sealed record SearchSince(DateOnly Day) : SearchFilter
{
    public override string Describe() => $"SINCE {Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
