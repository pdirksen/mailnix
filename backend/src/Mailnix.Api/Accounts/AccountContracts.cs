using System.ComponentModel.DataAnnotations;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;

namespace Mailnix.Api.Accounts;

public sealed record TestConnectionRequest(
    [Required, MaxLength(253)] string Host,
    [Range(1, 65535)] int Port,
    ImapSecurity Security,
    [Required, MaxLength(320)] string Username,
    [Required, MaxLength(1024)] string Password);

/// <summary>Result of a connection test. Connection problems are a normal outcome here, not an HTTP error.</summary>
public sealed record ConnectionTestResponse(
    bool Success,
    ConnectionErrorCode? ErrorCode,
    string? ErrorMessage,
    MailboxOverview? Mailbox);

public sealed record MailboxOverview(
    MailboxCapabilities Capabilities,
    string Delimiter,
    IReadOnlyList<MailFolderInfo> Folders,
    SpecialFolderMap SpecialFolders);

public sealed record CreateAccountRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(253)] string Host,
    [Range(1, 65535)] int Port,
    ImapSecurity Security,
    [Required, MaxLength(320)] string Username,
    [Required, MaxLength(1024)] string Password,
    [Required, MinLength(1)] IReadOnlyList<string> WatchedFolders,
    SpecialFolderMap? SpecialFolders = null,
    [Range(1, 100_000)] int BlastRadiusLimit = 200,
    [Range(1, 3650)] int? PurgeTrashAfterDays = null);

public sealed record UpdateAccountRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(253)] string Host,
    [Range(1, 65535)] int Port,
    ImapSecurity Security,
    [Required, MaxLength(320)] string Username,
    [Required, MinLength(1)] IReadOnlyList<string> WatchedFolders,
    SpecialFolderMap SpecialFolders,
    bool Enabled,
    [Range(1, 100_000)] int BlastRadiusLimit,
    [Range(1, 3650)] int? PurgeTrashAfterDays,
    [MaxLength(1024)] string? Password = null);

public sealed record AccountResponse(
    Guid Id,
    string Name,
    MailProvider Provider,
    string Host,
    int Port,
    ImapSecurity Security,
    string Username,
    bool Enabled,
    MailAccountStatus Status,
    ConnectionErrorCode? LastErrorCode,
    string? LastError,
    DateTimeOffset? LastCheckedAt,
    IReadOnlyList<string> WatchedFolders,
    SpecialFolderMap SpecialFolders,
    IReadOnlyList<string> Capabilities,
    int BlastRadiusLimit,
    int? PurgeTrashAfterDays,
    DateTimeOffset CreatedAt)
{
    public static AccountResponse From(MailAccount a) => new(
        a.Id,
        a.Name,
        a.Provider,
        a.Host,
        a.Port,
        a.Security,
        a.Username,
        a.Enabled,
        a.Status,
        a.LastErrorCode,
        a.LastError,
        a.LastCheckedAt,
        a.WatchedFolders,
        new SpecialFolderMap(a.SpecialFolders.Sent, a.SpecialFolders.Drafts, a.SpecialFolders.Trash, a.SpecialFolders.Junk, a.SpecialFolders.Archive),
        a.Capabilities,
        a.BlastRadiusLimit,
        a.PurgeTrashAfterDays,
        a.CreatedAt);
}

public sealed record FolderListResponse(string Delimiter, IReadOnlyList<MailFolderInfo> Folders, SpecialFolderMap SpecialFolders);
