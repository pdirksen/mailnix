using Mailnix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

/// <summary>
/// Queues runs for scheduled rules when their cron schedule is due, and Trash purges for accounts that have them
/// switched on. The account's watcher executes them like any other queued run (one connection per account).
/// </summary>
internal sealed partial class RuleScheduler(
    IServiceScopeFactory scopes,
    IWorkerSignals signals,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<RuleScheduler> logger) : BackgroundService
{
    private static readonly RunState[] Open = [RunState.Queued, RunState.Running, RunState.AwaitingConfirmation];

    private WorkerOptions Options => options.Value;

    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                LogTickFailed(ex);
            }

            try
            {
                await Task.Delay(Options.ScheduleCheckInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Queues everything that is due now.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var now = time.GetUtcNow();
        var woken = new HashSet<Guid>();

        // Queued runs and "last scheduled" times are saved together, so an occurrence is neither lost nor doubled.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var scheduled = await (
            from rule in db.Rules.AsNoTracking()
            join account in db.MailAccounts.AsNoTracking() on rule.AccountId equals account.Id
            join user in db.Users.AsNoTracking() on account.UserId equals user.Id
            where rule.Trigger == RuleTrigger.Scheduled && rule.Enabled && account.Enabled
            select new { Rule = rule, Account = account, user.TimeZone }).ToListAsync(ct);

        var due = scheduled.Where(s => RuleSchedule.NextRun(s.Rule, RuleSchedule.Zone(s.TimeZone)) <= now).ToList();
        if (due.Count > 0)
        {
            var accountIds = due.Select(d => d.Account.Id).Distinct().ToList();
            var open = await db.Runs.AsNoTracking()
                .Where(r => accountIds.Contains(r.AccountId) && r.Trigger == RunTrigger.Scheduled && Open.Contains(r.State))
                .ToListAsync(ct);

            foreach (var item in due)
            {
                var folders = item.Rule.SourceFolders.Count > 0 ? item.Rule.SourceFolders : item.Account.WatchedFolders;
                foreach (var folder in folders)
                {
                    // Still busy with (or waiting for confirmation of) the previous run: skip this occurrence.
                    if (open.Any(r => r.AccountId == item.Account.Id && r.Folder == folder && r.RuleIds?.Contains(item.Rule.Id) == true))
                    {
                        continue;
                    }

                    db.Runs.Add(NewRun(item.Account, RunTrigger.Scheduled, folder, now, run =>
                    {
                        run.RuleIds = [item.Rule.Id];
                        run.Limit = Options.MaxScheduledRunMessages;
                    }));
                }

                // Not through the tracked entity: that would count as an edit of the rule.
                await db.Rules.Where(r => r.Id == item.Rule.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.LastScheduledAt, now), ct);
                LogScheduled(item.Rule.Id, item.Account.Id);
                woken.Add(item.Account.Id);
            }
        }

        var purging = await db.MailAccounts.AsNoTracking().Where(a => a.Enabled && a.PurgeTrashAfterDays != null).ToListAsync(ct);
        foreach (var account in purging.Where(a => a.SpecialFolders.Trash is not null && (a.LastPurgeAt is not { } last || last + Options.PurgeInterval <= now)))
        {
            var busy = await db.Runs.AnyAsync(r => r.AccountId == account.Id && r.Trigger == RunTrigger.Purge && Open.Contains(r.State), ct);
            if (!busy)
            {
                db.Runs.Add(NewRun(account, RunTrigger.Purge, account.SpecialFolders.Trash!, now, _ => { }));
                woken.Add(account.Id);
            }

            await db.MailAccounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.LastPurgeAt, now), ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await CleanUpAsync(db, now, ct);
        foreach (var accountId in woken)
        {
            signals.Wake(accountId);
        }
    }

    /// <summary>Once a day: finished runs (with their action log) older than the retention period.</summary>
    private async Task CleanUpAsync(MailnixDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (now - _lastCleanup < TimeSpan.FromDays(1))
        {
            return;
        }

        _lastCleanup = now;
        if (await DeleteOldRunsAsync(db, now - Options.ActionLogRetention, ct) is var removed and > 0)
        {
            LogCleanedUp(removed);
        }
    }

    /// <summary>Deletes finished runs created before <paramref name="cutoff"/> and their action log; the stats counters stay.</summary>
    internal static async Task<int> DeleteOldRunsAsync(MailnixDbContext db, DateTimeOffset cutoff, CancellationToken ct)
    {
        RunState[] finished = [RunState.Completed, RunState.CompletedWithErrors, RunState.Failed, RunState.Cancelled, RunState.Expired];
        var old = await db.Runs.Where(r => r.CreatedAt < cutoff && finished.Contains(r.State)).Select(r => r.Id).ToListAsync(ct);
        foreach (var batch in old.Chunk(500))
        {
            await db.ActionLog.Where(a => batch.Contains(a.RunId)).ExecuteDeleteAsync(ct);
            await db.Runs.Where(r => batch.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        return old.Count;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} runs older than the retention period, with their action log")]
    private partial void LogCleanedUp(int count);

    private static RuleRun NewRun(MailAccount account, RunTrigger trigger, string folder, DateTimeOffset now, Action<RuleRun> configure)
    {
        var run = new RuleRun
        {
            Id = Guid.CreateVersion7(),
            TenantId = account.TenantId,
            UserId = account.UserId,
            AccountId = account.Id,
            Trigger = trigger,
            State = RunState.Queued,
            Folder = folder,
            CreatedAt = now,
        };
        configure(run);
        return run;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled rule {RuleId} of account {AccountId} is due; run queued")]
    private partial void LogScheduled(Guid ruleId, Guid accountId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not check scheduled rules")]
    private partial void LogTickFailed(Exception exception);
}
