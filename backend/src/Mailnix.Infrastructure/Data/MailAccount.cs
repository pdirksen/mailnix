using Mailnix.Infrastructure.Identity;
using Mailnix.Mail;

namespace Mailnix.Infrastructure.Data;

public enum MailProvider
{
    Imap,
    Google,
    Microsoft,
}

public enum MailAccountStatus
{
    /// <summary>Not checked since the last settings change.</summary>
    Unknown,
    Ok,
    Error,
}

public class MailAccount
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public required string Name { get; set; }

    public MailProvider Provider { get; set; } = MailProvider.Imap;

    public required string Host { get; set; }

    public int Port { get; set; }

    public ImapSecurity Security { get; set; }

    public required string Username { get; set; }

    /// <summary>Password (or later: OAuth refresh token), encrypted via <see cref="MailSecretProtector"/>. Never leaves the server.</summary>
    public required string EncryptedSecret { get; set; }

    public bool Enabled { get; set; } = true;

    public MailAccountStatus Status { get; set; }

    public ConnectionErrorCode? LastErrorCode { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Folders whose new mail is processed by rules.</summary>
    public List<string> WatchedFolders { get; set; } = ["INBOX"];

    public string FolderDelimiter { get; set; } = "/";

    public MailAccountSpecialFolders SpecialFolders { get; set; } = new();

    /// <summary>Server capabilities as detected on the last successful check.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>A run that would move/delete more messages than this pauses and asks for confirmation.</summary>
    public int BlastRadiusLimit { get; set; } = 200;

    /// <summary>Permanently purge Trash after this many days; null = never (the default).</summary>
    public int? PurgeTrashAfterDays { get; set; }

    /// <summary>When the scheduler last queued a Trash purge.</summary>
    public DateTimeOffset? LastPurgeAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Where mailnix moves messages for delete/spam/archive actions. Detected on setup, editable by the user.</summary>
public class MailAccountSpecialFolders
{
    public string? Sent { get; set; }

    public string? Drafts { get; set; }

    public string? Trash { get; set; }

    public string? Junk { get; set; }

    public string? Archive { get; set; }
}
