using System.Net.Sockets;
using System.Security.Cryptography;
using MailKit;
using MailKit.Net.Imap;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

/// <summary>
/// Keeps one connection to an account's mailbox: runs queued jobs, applies rules to new mail in the watched
/// folders, then waits (IDLE) for more. Reconnects with exponential backoff.
/// </summary>
internal sealed partial class MailboxWatcher(
    Guid accountId,
    IServiceScopeFactory scopes,
    IMailboxSessionFactory sessions,
    IWorkerSignals signals,
    ILiveNotifier notifier,
    MailSecretProtector secrets,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<MailboxWatcher> logger)
{
    private WorkerOptions Options => options.Value;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = Options.MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var account = await LoadAccountAsync(ct);
                if (account is null)
                {
                    return;
                }

                ImapConnectionSettings settings;
                try
                {
                    settings = new ImapConnectionSettings(account.Host, account.Port, account.Security, account.Username, secrets.Unprotect(account.EncryptedSecret));
                }
                catch (CryptographicException)
                {
                    // Nothing to retry until the user enters the password again (which restarts the watcher).
                    await SetStatusAsync(account, MailAccountStatus.Error, ConnectionErrorCode.Unknown, "The stored password can no longer be decrypted. Enter it again in the account settings.", ct);
                    return;
                }

                await using var session = await sessions.OpenAsync(settings, ct);
                await SetStatusAsync(account, MailAccountStatus.Ok, null, null, ct);
                backoff = Options.MinBackoff;
                LogConnected(accountId);

                while (!ct.IsCancellationRequested)
                {
                    // Take the wake token before looking for queued jobs, so a job queued meanwhile ends the idle.
                    var wake = signals.WakeToken(accountId);
                    account = await PassAsync(session, ct);
                    if (account is null)
                    {
                        return;
                    }

                    var primary = account.WatchedFolders.FirstOrDefault() ?? "INBOX";
                    var timeout = account.WatchedFolders.Count > 1 ? Options.PollInterval : Options.IdleTimeout;
                    await session.IdleAsync(primary, timeout, wake, ct);
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (MailConnectionException ex)
            {
                await SetStatusSafelyAsync(MailAccountStatus.Error, ex.Code, ex.Message, ct);
                delay = ex.Code == ConnectionErrorCode.AuthenticationFailed ? Options.AuthenticationFailureBackoff : backoff;
                LogConnectionFailed(accountId, ex.Code, delay);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ImapProtocolException or ImapCommandException or ServiceNotConnectedException or ProtocolException)
            {
                var error = MailConnectionErrors.Classify(ex);
                await SetStatusSafelyAsync(MailAccountStatus.Error, error.Code, error.Message, ct);
                delay = backoff;
                LogConnectionLost(accountId, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bug must not kill the watcher for good; retry later.
                LogUnexpected(accountId, ex);
                delay = backoff;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, Options.MaxBackoff.Ticks));
            try
            {
                await Task.Delay(delay, time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: queued jobs, then new mail in every watched folder. Returns the account as of this pass.</summary>
    private async Task<MailAccount?> PassAsync(IMailboxSession session, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var account = await db.MailAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.Enabled, ct);
        if (account is null)
        {
            return null;
        }

        var processor = scope.ServiceProvider.GetRequiredService<RunProcessor>();
        await processor.ProcessQueuedAsync(session, account, scope.ServiceProvider.GetRequiredService<UndoProcessor>(), ct);

        var missing = new List<string>();
        foreach (var folder in account.WatchedFolders)
        {
            try
            {
                await processor.ProcessNewMailAsync(session, account, folder, ct);
            }
            catch (Mail.FolderNotFoundException)
            {
                missing.Add(folder);
            }
        }

        if (missing.Count > 0)
        {
            await SetStatusAsync(account, MailAccountStatus.Error, ConnectionErrorCode.Unknown, $"Watched folder not found on the server: {string.Join(", ", missing)}", ct);
        }
        else if (account.Status != MailAccountStatus.Ok)
        {
            await SetStatusAsync(account, MailAccountStatus.Ok, null, null, ct);
        }

        return account;
    }

    private async Task<MailAccount?> LoadAccountAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.Enabled, ct);
    }

    private async Task SetStatusSafelyAsync(MailAccountStatus status, ConnectionErrorCode? code, string? error, CancellationToken ct)
    {
        try
        {
            if (await LoadAccountAsync(ct) is { } account)
            {
                await SetStatusAsync(account, status, code, error, ct);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            LogUnexpected(accountId, ex);
        }
    }

    /// <summary>Updates the account's status without touching UpdatedAt (which would restart this watcher).</summary>
    private async Task SetStatusAsync(MailAccount account, MailAccountStatus status, ConnectionErrorCode? code, string? error, CancellationToken ct)
    {
        error = error is { Length: > 1000 } ? error[..1000] : error;
        if (account.Status == status && account.LastErrorCode == code && account.LastError == error)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var now = time.GetUtcNow();
        await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts
            .Where(a => a.Id == account.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.LastErrorCode, code)
                .SetProperty(a => a.LastError, error)
                .SetProperty(a => a.LastCheckedAt, now), ct);
        account.Status = status;
        account.LastErrorCode = code;
        account.LastError = error;
        await notifier.NotifyAsync(account.UserId, LiveEvents.AccountStatusChanged, new AccountStatusEvent(account.Id, status, code, error));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching account {AccountId}")]
    private partial void LogConnected(Guid accountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account {AccountId}: connection failed ({Code}), retrying in {Delay}")]
    private partial void LogConnectionFailed(Guid accountId, ConnectionErrorCode code, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Account {AccountId}: connection lost")]
    private partial void LogConnectionLost(Guid accountId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Account {AccountId}: unexpected error in the mailbox watcher")]
    private partial void LogUnexpected(Guid accountId, Exception exception);
}
