using MailKit;
using MailKit.Net.Imap;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using MimeKit;

namespace Mailnix.Mail;

public sealed record FolderSample(string Folder, int TotalMessages, IReadOnlyList<MessageSnapshot> Messages);

public interface IMessageReader
{
    /// <summary>
    /// Reads the newest messages of a folder without changing anything (the folder is opened read-only,
    /// so not even the \Seen flag changes). Fetches only what <paramref name="requirements"/> asks for.
    /// </summary>
    Task<FolderSample> ReadLatestAsync(ImapConnectionSettings settings, string folder, int limit, FetchRequirements requirements, CancellationToken ct);

    /// <summary>Message-IDs of the newest messages in a folder (used with the Sent folder for "reply to my mail").</summary>
    Task<IReadOnlySet<string>> ReadMessageIdsAsync(ImapConnectionSettings settings, string folder, int limit, CancellationToken ct);
}

public sealed class FolderNotFoundException(string folder) : Exception($"Folder '{folder}' does not exist.")
{
    public string Folder { get; } = folder;
}

internal sealed class MessageReader(IImapConnector connector) : IMessageReader
{
    /// <summary>Plain-text bodies are cut here; rules match on the beginning of long messages only.</summary>
    internal const int MaxBodyLength = 64 * 1024;

    public async Task<FolderSample> ReadLatestAsync(
        ImapConnectionSettings settings,
        string folder,
        int limit,
        FetchRequirements requirements,
        CancellationToken ct)
    {
        using var client = await connector.ConnectAsync(settings, ct);
        try
        {
            var mailFolder = await OpenReadOnlyAsync(client, folder, ct);
            if (mailFolder.Count == 0)
            {
                return new FolderSample(mailFolder.FullName, 0, []);
            }

            var first = Math.Max(0, mailFolder.Count - limit);
            var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                | MessageSummaryItems.InternalDate | MessageSummaryItems.Size;
            if (requirements.HasFlag(FetchRequirements.Headers))
            {
                items |= MessageSummaryItems.Headers;
            }

            if (requirements.HasFlag(FetchRequirements.BodyStructure) || requirements.HasFlag(FetchRequirements.Body))
            {
                items |= MessageSummaryItems.BodyStructure;
            }

            var summaries = await mailFolder.FetchAsync(first, -1, items, ct);
            var snapshots = new List<MessageSnapshot>(summaries.Count);
            foreach (var summary in summaries.OrderByDescending(s => s.UniqueId.Id))
            {
                var body = requirements.HasFlag(FetchRequirements.Body) ? await ReadBodyTextAsync(mailFolder, summary, ct) : null;
                snapshots.Add(ToSnapshot(summary, mailFolder.FullName, requirements, body));
            }

            return new FolderSample(mailFolder.FullName, mailFolder.Count, snapshots);
        }
        catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException or IOException)
        {
            throw ImapConnector.Classify(ex, timedOut: false);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    public async Task<IReadOnlySet<string>> ReadMessageIdsAsync(ImapConnectionSettings settings, string folder, int limit, CancellationToken ct)
    {
        using var client = await connector.ConnectAsync(settings, ct);
        try
        {
            var mailFolder = await OpenReadOnlyAsync(client, folder, ct);
            if (mailFolder.Count == 0)
            {
                return new HashSet<string>();
            }

            var summaries = await mailFolder.FetchAsync(Math.Max(0, mailFolder.Count - limit), -1, MessageSummaryItems.Envelope, ct);
            return summaries.Select(s => s.Envelope?.MessageId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    internal static MessageSnapshot ToSnapshot(IMessageSummary summary, string folder, FetchRequirements requirements, string? bodyText)
    {
        var envelope = summary.Envelope;
        var flags = summary.Flags ?? MessageFlags.None;
        return new MessageSnapshot
        {
            Uid = summary.UniqueId.Id,
            Folder = folder,
            MessageId = envelope?.MessageId,
            From = envelope is null ? null : MessageSnapshotFactory.Addresses(envelope.From) is [var from, ..] ? from : null,
            To = envelope is null ? [] : MessageSnapshotFactory.Addresses(envelope.To),
            Cc = envelope is null ? [] : MessageSnapshotFactory.Addresses(envelope.Cc),
            ReplyTo = envelope is null ? [] : MessageSnapshotFactory.Addresses(envelope.ReplyTo),
            Subject = envelope?.Subject,
            Date = envelope?.Date,
            ReceivedAt = summary.InternalDate,
            Size = summary.Size,
            IsSeen = flags.HasFlag(MessageFlags.Seen),
            IsFlagged = flags.HasFlag(MessageFlags.Flagged),
            IsAnswered = flags.HasFlag(MessageFlags.Answered),
            Keywords = summary.Keywords is null ? [] : [.. summary.Keywords],
            Headers = requirements.HasFlag(FetchRequirements.Headers) && summary.Headers is not null
                ? [.. summary.Headers.Select(h => new KeyValuePair<string, string>(h.Field, h.Value))]
                : null,
            Attachments = summary.Body is null
                ? null
                : [.. summary.Attachments.Select(a => new AttachmentInfo(a.FileName, a.ContentType.MimeType.ToLowerInvariant(), a.Octets))],
            BodyText = bodyText,
        };
    }

    private static async Task<IMailFolder> OpenReadOnlyAsync(ImapClient client, string folder, CancellationToken ct)
    {
        IMailFolder mailFolder;
        try
        {
            mailFolder = folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(folder, ct);
        }
        catch (MailKit.FolderNotFoundException)
        {
            throw new Mail.FolderNotFoundException(folder);
        }

        // EXAMINE: reading never marks messages as seen.
        await mailFolder.OpenAsync(FolderAccess.ReadOnly, ct);
        return mailFolder;
    }

    private static async Task<string?> ReadBodyTextAsync(IMailFolder folder, IMessageSummary summary, CancellationToken ct)
    {
        var part = summary.TextBody ?? summary.HtmlBody;
        if (part is null)
        {
            return null;
        }

        // BODY.PEEK: does not set \Seen.
        if (await folder.GetBodyPartAsync(summary.UniqueId, part, ct) is not TextPart text)
        {
            return null;
        }

        var content = text.IsHtml ? MessageSnapshotFactory.HtmlToText(text.Text) : text.Text;
        return content is { Length: > MaxBodyLength } ? content[..MaxBodyLength] : content;
    }

    private static async Task DisconnectQuietlyAsync(ImapClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(quit: true, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or ImapProtocolException or OperationCanceledException or ServiceNotConnectedException)
        {
            // Best effort.
        }
    }
}
