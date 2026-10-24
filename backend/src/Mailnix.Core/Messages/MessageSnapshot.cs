namespace Mailnix.Core.Messages;

public sealed record AttachmentInfo(string? FileName, string MimeType, long Size)
{
    /// <summary>Lowercase extension without dot, e.g. "pdf"; empty if none.</summary>
    public string Extension =>
        FileName is null || Path.GetExtension(FileName) is not { Length: > 1 } ext ? string.Empty : ext[1..].ToLowerInvariant();
}

/// <summary>
/// Everything the rule engine may look at for one message. Built from what was fetched from the server:
/// envelope, flags, size and dates are always present; headers, attachment structure and body only when
/// a rule needs them (see <see cref="Rules.FetchRequirements"/>) and are null otherwise.
/// </summary>
public sealed record MessageSnapshot
{
    public uint Uid { get; init; }

    public string Folder { get; init; } = "INBOX";

    public string? MessageId { get; init; }

    public EmailAddress? From { get; init; }

    public IReadOnlyList<EmailAddress> To { get; init; } = [];

    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];

    public IReadOnlyList<EmailAddress> ReplyTo { get; init; } = [];

    public string? Subject { get; init; }

    /// <summary>The Date header (when the sender says it was sent).</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>The server's INTERNALDATE (when it arrived).</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    public long? Size { get; init; }

    public bool IsSeen { get; init; }

    public bool IsFlagged { get; init; }

    public bool IsAnswered { get; init; }

    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>All header fields in message order (topmost first), or null if not fetched.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; init; }

    public IReadOnlyList<AttachmentInfo>? Attachments { get; init; }

    /// <summary>Plain-text body (HTML converted to text), or null if not fetched.</summary>
    public string? BodyText { get; init; }

    /// <summary>Arrival time, falling back to the Date header.</summary>
    public DateTimeOffset? EffectiveDate => ReceivedAt ?? Date;

    public IEnumerable<string> HeaderValues(string name) =>
        Headers is null ? [] : Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    public string? FirstHeader(string name) => HeaderValues(name).FirstOrDefault();
}
