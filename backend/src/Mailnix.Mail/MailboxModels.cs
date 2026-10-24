namespace Mailnix.Mail;

public enum SpecialFolderKind
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
    All,
    Flagged,
}

/// <summary>Server features mailnix cares about (others are listed in <see cref="All"/>).</summary>
public sealed record MailboxCapabilities(
    bool Move,
    bool UidPlus,
    bool CondStore,
    bool QResync,
    bool Idle,
    bool SpecialUse,
    bool GmailExtensions,
    bool StatusSize,
    IReadOnlyList<string> All);

public sealed record MailFolderInfo(
    string Path,
    string Name,
    string? ParentPath,
    bool CanSelect,
    SpecialFolderKind? SpecialUse,
    int? Messages,
    int? Unread);

/// <summary>Where the well-known folders live on this server; null when the server has none.</summary>
public sealed record SpecialFolderMap(string? Sent, string? Drafts, string? Trash, string? Junk, string? Archive)
{
    public static readonly SpecialFolderMap Empty = new(null, null, null, null, null);

    public string? this[SpecialFolderKind kind] => kind switch
    {
        SpecialFolderKind.Sent => Sent,
        SpecialFolderKind.Drafts => Drafts,
        SpecialFolderKind.Trash => Trash,
        SpecialFolderKind.Junk => Junk,
        SpecialFolderKind.Archive => Archive,
        _ => null,
    };
}

public sealed record MailboxProbeResult(
    MailboxCapabilities Capabilities,
    char Delimiter,
    IReadOnlyList<MailFolderInfo> Folders,
    SpecialFolderMap SpecialFolders);
