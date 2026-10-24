using MailKit;
using MailKit.Net.Imap;

namespace Mailnix.Mail;

public interface IMailboxProbe
{
    /// <summary>
    /// Connects, reads capabilities and the folder list (optionally with message counts), then disconnects.
    /// Read-only. Throws <see cref="MailConnectionException"/> when the mailbox cannot be reached.
    /// </summary>
    Task<MailboxProbeResult> ProbeAsync(ImapConnectionSettings settings, bool includeCounts, CancellationToken cancellationToken);
}

internal sealed class MailboxProbe(IImapConnector connector) : IMailboxProbe
{
    public async Task<MailboxProbeResult> ProbeAsync(ImapConnectionSettings settings, bool includeCounts, CancellationToken cancellationToken)
    {
        using var client = await connector.ConnectAsync(settings, cancellationToken);
        try
        {
            var capabilities = ReadCapabilities(client.Capabilities);
            var personal = client.PersonalNamespaces.FirstOrDefault()
                ?? new FolderNamespace(client.Inbox.DirectorySeparator, string.Empty);

            var items = includeCounts ? StatusItems.Count | StatusItems.Unread : StatusItems.None;
            var listed = await client.GetFoldersAsync(personal, items, subscribedOnly: false, cancellationToken);

            // Some servers list INBOX outside the personal namespace (e.g. namespace "INBOX." on Courier/Dovecot setups).
            var inboxListed = listed.Any(f => IsInbox(f, client));
            IReadOnlyList<IMailFolder> folders = inboxListed ? [.. listed] : [client.Inbox, .. listed];
            if (!inboxListed && includeCounts)
            {
                await client.Inbox.StatusAsync(items, cancellationToken);
            }

            var specialFolders = SpecialFolderDetector.Detect(
                [.. folders.Select(f => new SpecialFolderDetector.Candidate(f.FullName, f.Name, CanSelect(f), FlaggedKind(f)))]);

            var infos = folders
                .Select(f => ToInfo(f, IsInbox(f, client), specialFolders, includeCounts))
                .OrderBy(f => f.SpecialUse == SpecialFolderKind.Inbox ? 0 : 1)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new MailboxProbeResult(capabilities, personal.DirectorySeparator, infos, specialFolders);
        }
        finally
        {
            await DisconnectQuietlyAsync(client);
        }
    }

    internal static MailboxCapabilities ReadCapabilities(ImapCapabilities capabilities) => new(
        Move: capabilities.HasFlag(ImapCapabilities.Move),
        UidPlus: capabilities.HasFlag(ImapCapabilities.UidPlus),
        CondStore: capabilities.HasFlag(ImapCapabilities.CondStore),
        QResync: capabilities.HasFlag(ImapCapabilities.QuickResync),
        Idle: capabilities.HasFlag(ImapCapabilities.Idle),
        SpecialUse: capabilities.HasFlag(ImapCapabilities.SpecialUse),
        GmailExtensions: capabilities.HasFlag(ImapCapabilities.GMailExt1),
        StatusSize: capabilities.HasFlag(ImapCapabilities.StatusSize),
        All: [.. Enum.GetValues<ImapCapabilities>()
            .Where(c => c != ImapCapabilities.None && capabilities.HasFlag(c))
            .Select(c => c.ToString())]);

    private static readonly SpecialFolderKind[] MappedKinds =
        [SpecialFolderKind.Sent, SpecialFolderKind.Drafts, SpecialFolderKind.Trash, SpecialFolderKind.Junk, SpecialFolderKind.Archive];

    private static MailFolderInfo ToInfo(IMailFolder folder, bool isInbox, SpecialFolderMap specialFolders, bool includeCounts)
    {
        var canSelect = CanSelect(folder);
        var parent = folder.ParentFolder?.FullName;
        return new MailFolderInfo(
            Path: folder.FullName,
            Name: folder.Name,
            ParentPath: string.IsNullOrEmpty(parent) ? null : parent,
            CanSelect: canSelect,
            SpecialUse: KindOf(folder, isInbox, specialFolders),
            Messages: includeCounts && canSelect ? folder.Count : null,
            Unread: includeCounts && canSelect ? folder.Unread : null);
    }

    private static SpecialFolderKind? KindOf(IMailFolder folder, bool isInbox, SpecialFolderMap specialFolders)
    {
        if (isInbox)
        {
            return SpecialFolderKind.Inbox;
        }

        foreach (var kind in MappedKinds)
        {
            if (specialFolders[kind] == folder.FullName)
            {
                return kind;
            }
        }

        // Kinds without a slot in the map (e.g. Gmail's "All Mail") are still worth showing.
        return FlaggedKind(folder) is SpecialFolderKind.All or SpecialFolderKind.Flagged ? FlaggedKind(folder) : null;
    }

    private static bool IsInbox(IMailFolder folder, ImapClient client) =>
        folder.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase);

    private static bool CanSelect(IMailFolder folder) =>
        !folder.Attributes.HasFlag(FolderAttributes.NoSelect) && !folder.Attributes.HasFlag(FolderAttributes.NonExistent);

    private static SpecialFolderKind? FlaggedKind(IMailFolder folder) => folder.Attributes switch
    {
        var a when a.HasFlag(FolderAttributes.Sent) => SpecialFolderKind.Sent,
        var a when a.HasFlag(FolderAttributes.Drafts) => SpecialFolderKind.Drafts,
        var a when a.HasFlag(FolderAttributes.Trash) => SpecialFolderKind.Trash,
        var a when a.HasFlag(FolderAttributes.Junk) => SpecialFolderKind.Junk,
        var a when a.HasFlag(FolderAttributes.Archive) => SpecialFolderKind.Archive,
        var a when a.HasFlag(FolderAttributes.All) => SpecialFolderKind.All,
        var a when a.HasFlag(FolderAttributes.Flagged) => SpecialFolderKind.Flagged,
        _ => null,
    };

    private static async Task DisconnectQuietlyAsync(ImapClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(quit: true, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or ImapProtocolException or OperationCanceledException or ServiceNotConnectedException)
        {
            // Best effort; the connection is closed on dispose anyway.
        }
    }
}
