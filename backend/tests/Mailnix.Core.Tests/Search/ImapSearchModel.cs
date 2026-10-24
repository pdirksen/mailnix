using Mailnix.Core.Messages;
using Mailnix.Core.Rules;

namespace Mailnix.Core.Tests.Search;

/// <summary>
/// Reference semantics of IMAP SEARCH (RFC 3501, 6.4.4) on snapshots, as servers implement it: case-insensitive
/// substring matches on whole header fields (display name and address), arrival dates as UTC days.
/// Used by the planner's property tests and by the fake mail server of the integration tests.
/// </summary>
internal static class ImapSearchModel
{
    public static bool Matches(SearchFilter? filter, MessageSnapshot message) => filter switch
    {
        null => true,
        SearchAnd and => and.Children.All(c => Matches(c, message)),
        SearchOr or => or.Children.Any(c => Matches(c, message)),
        SearchNot not => !Matches(not.Inner, message),
        SearchText text => Fields(text, message).Any(v => v.Contains(text.Value, StringComparison.OrdinalIgnoreCase)),
        SearchFlagIs { Flag: SearchFlagKind.Seen } flag => message.IsSeen == flag.Set,
        SearchFlagIs { Flag: SearchFlagKind.Flagged } flag => message.IsFlagged == flag.Set,
        SearchFlagIs { Flag: SearchFlagKind.Answered } flag => message.IsAnswered == flag.Set,
        SearchKeyword keyword => message.Keywords.Contains(keyword.Keyword, StringComparer.OrdinalIgnoreCase),
        SearchLarger larger => (message.Size ?? 0) > larger.Bytes,
        SearchSmaller smaller => (message.Size ?? 0) < smaller.Bytes,
        SearchBefore before => Day(message) is { } day && day < before.Day,
        SearchSince since => Day(message) is { } day && day >= since.Day,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown search filter."),
    };

    private static IEnumerable<string> Fields(SearchText text, MessageSnapshot message) => text.Field switch
    {
        SearchTextField.From => message.From is null ? [] : [message.From.ToString()],
        SearchTextField.To => [string.Join(", ", message.To)],
        SearchTextField.Cc => [string.Join(", ", message.Cc)],
        SearchTextField.Subject => [message.Subject ?? string.Empty],
        _ => message.HeaderValues(text.HeaderName!),
    };

    private static DateOnly? Day(MessageSnapshot message) =>
        message.EffectiveDate is { } date ? DateOnly.FromDateTime(date.UtcDateTime) : null;
}
