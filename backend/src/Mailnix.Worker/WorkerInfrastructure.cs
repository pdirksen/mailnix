using System.Collections.Concurrent;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;

namespace Mailnix.Worker;

public sealed class WorkerOptions
{
    public const string Section = "Mailnix:Worker";

    /// <summary>Run mailbox watchers in this process (AllInOne and Worker modes). Tests switch it off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the supervisor checks for added, removed or changed accounts.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>IDLE is renewed after this long (servers drop IDLE after ~30 minutes).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(9);

    /// <summary>With several watched folders, the non-IDLE ones are checked this often.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>At most this many new messages per folder and pass; the rest follows in the next pass.</summary>
    public int MaxNewMessagesPerPass { get; set; } = 200;

    /// <summary>Upper bound for "run now" over existing mail.</summary>
    public int MaxManualRunMessages { get; set; } = 1000;

    public TimeSpan ConfirmationExpiry { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan MinBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>After a rejected login, wait longer: retrying quickly can get the server to block us (fail2ban).</summary>
    public TimeSpan AuthenticationFailureBackoff { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Messages mailnix moved into a folder are not processed again by that folder's rules within this window.</summary>
    public TimeSpan LoopGuardWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Let the mail server preselect candidates for manual and scheduled runs (IMAP SEARCH).</summary>
    public bool SearchPushdown { get; set; } = true;

    /// <summary>Messages are fetched in batches of this size.</summary>
    public int FetchBatchSize { get; set; } = 500;

    /// <summary>How often the scheduler looks for due scheduled rules and Trash purges.</summary>
    public TimeSpan ScheduleCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A scheduled run checks at most this many candidates (newest first); the rest follows next time.</summary>
    public int MaxScheduledRunMessages { get; set; } = 5000;

    /// <summary>Trash is checked for messages to purge this often.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>At most this many messages are purged per run.</summary>
    public int MaxPurgePerRun { get; set; } = 5000;

    /// <summary>
    /// Runs and their action log are kept this long; older ones can no longer be undone. The stats keep their
    /// daily counters regardless.
    /// </summary>
    public TimeSpan ActionLogRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>A worker holds a mailbox this long without renewing; then another worker may take it over.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Most mailboxes one worker process watches.</summary>
    public int MaxAccountsPerWorker { get; set; } = 500;

    /// <summary>How often the worker looks for runs queued by an API process.</summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Lets the API nudge the worker: new jobs for an account, or changed accounts.</summary>
public interface IWorkerSignals
{
    /// <summary>Wake the account's watcher (e.g. a run was queued).</summary>
    void Wake(Guid accountId);

    /// <summary>Accounts were added, changed or removed.</summary>
    void AccountsChanged();

    CancellationToken WakeToken(Guid accountId);

    Task WaitForAccountChangesAsync(TimeSpan timeout, CancellationToken ct);
}

/// <remarks>
/// The watcher takes a fresh token at the start of each pass, before looking for queued jobs, and idles on it.
/// A job queued before that is found by the pass; a job queued after it cancels the token and ends the idle.
/// </remarks>
internal sealed class WorkerSignals : IWorkerSignals, IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _wake = new();
    private readonly SemaphoreSlim _accountsChanged = new(0);

    public void Wake(Guid accountId) => _wake.GetOrAdd(accountId, _ => new CancellationTokenSource()).Cancel();

    public void AccountsChanged()
    {
        if (_accountsChanged.CurrentCount == 0)
        {
            _accountsChanged.Release();
        }
    }

    public CancellationToken WakeToken(Guid accountId) =>
        _wake.AddOrUpdate(accountId, _ => new CancellationTokenSource(), (_, current) => current.IsCancellationRequested ? new CancellationTokenSource() : current).Token;

    public async Task WaitForAccountChangesAsync(TimeSpan timeout, CancellationToken ct) => await _accountsChanged.WaitAsync(timeout, ct);

    public void Dispose()
    {
        _accountsChanged.Dispose();
        foreach (var source in _wake.Values)
        {
            source.Dispose();
        }
    }
}

public static class LiveEvents
{
    public const string AccountStatusChanged = "accountStatusChanged";
    public const string RunUpdated = "runUpdated";
}

public sealed record AccountStatusEvent(Guid AccountId, MailAccountStatus Status, ConnectionErrorCode? ErrorCode, string? Error);

public sealed record RunEvent(Guid RunId, Guid AccountId, RunState State, RunTrigger Trigger);

/// <summary>Pushes events to the user's browsers (SignalR in the API process; nothing in a worker-only process).</summary>
public interface ILiveNotifier
{
    Task NotifyAsync(Guid userId, string eventName, object payload);
}

internal sealed class NullLiveNotifier : ILiveNotifier
{
    public Task NotifyAsync(Guid userId, string eventName, object payload) => Task.CompletedTask;
}
