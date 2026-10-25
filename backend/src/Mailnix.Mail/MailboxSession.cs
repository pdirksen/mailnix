using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using MimeKit;

namespace Mailnix.Mail;

public sealed record FolderStatus(string Folder, uint UidValidity, uint UidNext, int Count);

/// <summary>Size of a selectable folder (IMAP STATUS).</summary>
public sealed record FolderCount(string Folder, int Messages, int Unread);

/// <summary>The actions planned for one message.</summary>
public sealed record MessageActions(MessageSnapshot Message, IReadOnlyList<PlannedAction> Actions);

/// <param name="TargetUid">For copies and moves: the message's UID in the target folder (UIDPLUS), used for undo.</param>
public sealed record ActionOutcome(
    uint Uid,
    PlannedAction Action,
    bool Success,
    string? Error = null,
    uint? TargetUid = null,
    uint? TargetUidValidity = null);

/// <param name="Uids">Matching UIDs, ascending.</param>
/// <param name="Filtered">False when the server rejected the search and all messages were returned instead.</param>
public sealed record SearchResult(IReadOnlyList<uint> Uids, bool Filtered);

public enum IdleOutcome
{
    NewMessages,
    TimedOut,

    /// <summary>Woken up from outside (e.g. a queued job).</summary>
    Woken,
}

/// <summary>
/// One authenticated connection to a mailbox with the operations rule execution and undo need.
/// Not thread-safe; one session per account at a time.
/// </summary>
public interface IMailboxSession : IAsyncDisposable
{
    MailboxCapabilities Capabilities { get; }

    char Delimiter { get; }

    /// <summary>Selects (writable) or examines a folder. Throws <see cref="FolderNotFoundException"/>.</summary>
    Task<FolderStatus> OpenAsync(string folder, bool writable, CancellationToken ct);

    /// <summary>Messages with a UID above <paramref name="afterUid"/>, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<MessageSnapshot>> FetchNewAsync(string folder, uint afterUid, int max, FetchRequirements requirements, CancellationToken ct);

    /// <summary>The newest messages, newest first.</summary>
    Task<IReadOnlyList<MessageSnapshot>> FetchLatestAsync(string folder, int limit, FetchRequirements requirements, CancellationToken ct);

    /// <summary>
    /// UIDs of the messages matching <paramref name="filter"/> (null: all messages). If the server cannot run the
    /// search, all UIDs are returned (<see cref="SearchResult.Filtered"/> = false): the filter only narrows down.
    /// </summary>
    Task<SearchResult> SearchAsync(string folder, SearchFilter? filter, CancellationToken ct);

    /// <summary>Messages with the given UIDs (missing ones are left out).</summary>
    Task<IReadOnlyList<MessageSnapshot>> FetchAsync(string folder, IReadOnlyList<uint> uids, FetchRequirements requirements, CancellationToken ct);

    /// <summary>
    /// Runs planned actions on messages of <paramref name="folder"/>: flags and copies first, then moves.
    /// Missing target folders are created. One outcome per message and action.
    /// </summary>
    Task<IReadOnlyList<ActionOutcome>> ExecuteAsync(string folder, IReadOnlyList<MessageActions> work, CancellationToken ct);

    /// <summary>Waits for new messages in a folder (IDLE, or polling when the server has no IDLE).</summary>
    Task<IdleOutcome> IdleAsync(string folder, TimeSpan timeout, CancellationToken wake, CancellationToken ct);

    Task<IReadOnlySet<string>> MessageIdsAsync(string folder, int limit, CancellationToken ct);

    /// <summary>Message and unread counts of every selectable folder (read-only).</summary>
    Task<IReadOnlyList<FolderCount>> FolderCountsAsync(CancellationToken ct);

    /// <summary>Current UID of a message: the recorded UID if still valid, otherwise found by Message-ID; null if gone.</summary>
    Task<uint?> LocateAsync(string folder, uint uidValidity, uint uid, string? messageId, CancellationToken ct);

    /// <summary>Moves one message; returns its UID in the target folder when the server reports it.</summary>
    Task<uint?> MoveAsync(string folder, uint uid, string target, CancellationToken ct);

    /// <summary>Sets or clears flags and keywords on one message (null = leave unchanged).</summary>
    Task SetFlagsAsync(string folder, uint uid, bool? seen, bool? flagged, IReadOnlyCollection<string> addKeywords, IReadOnlyCollection<string> removeKeywords, CancellationToken ct);

    /// <summary>Permanently removes exactly this message. False if the server cannot expunge single messages (no UIDPLUS).</summary>
    Task<bool> ExpungeAsync(string folder, uint uid, CancellationToken ct);

    /// <summary>Permanently removes exactly these messages. False if the server cannot expunge single messages (no UIDPLUS).</summary>
    Task<bool> PurgeAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct);

    Task<uint?> AppendAsync(string folder, MimeMessage message, CancellationToken ct);

    Task CreateFolderAsync(string path, CancellationToken ct);

    Task DeleteFolderAsync(string path, CancellationToken ct);
}

public interface IMailboxSessionFactory
{
    /// <summary>Connects and authenticates. Throws <see cref="MailConnectionException"/>.</summary>
    Task<IMailboxSession> OpenAsync(ImapConnectionSettings settings, CancellationToken ct);
}
