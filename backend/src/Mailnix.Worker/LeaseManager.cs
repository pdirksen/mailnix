using Mailnix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

/// <summary>This worker process: one id per process run, used for leases and the system overview.</summary>
public sealed class WorkerIdentity
{
    public string Id { get; } = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";

    public string Host { get; } = Environment.MachineName;
}

/// <summary>
/// Distributes mailboxes among worker processes through <see cref="MailboxLease"/> rows: every update is
/// conditional, so two workers can never hold the same mailbox. Workers take about an equal share; a worker
/// that stops renewing loses its leases after <see cref="WorkerOptions.LeaseDuration"/>.
/// </summary>
internal sealed class LeaseManager(MailnixDbContext db, WorkerIdentity me, IOptions<WorkerOptions> options, TimeProvider time)
{
    /// <summary>Workers seen this recently count when sharing out the mailboxes.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

    private WorkerOptions Options => options.Value;

    /// <summary>Heartbeat, renew, claim and balance. Returns the accounts this worker holds, with their last change.</summary>
    public async Task<Dictionary<Guid, DateTimeOffset>> ReconcileAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await HeartbeatAsync(now, ct);
        await EnsureLeaseRowsAsync(ct);

        var until = now + Options.LeaseDuration;
        var mine = db.MailboxLeases.Where(l => l.WorkerId == me.Id);

        // Paused or deleted accounts are given back; the rest is renewed.
        await mine.Where(l => !db.MailAccounts.Any(a => a.Id == l.AccountId && a.Enabled))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.WorkerId, (string?)null), ct);
        await mine.ExecuteUpdateAsync(u => u.SetProperty(l => l.ExpiresAt, until), ct);

        var enabled = await db.MailAccounts.CountAsync(a => a.Enabled, ct);
        var workers = Math.Max(1, await db.Workers.CountAsync(w => w.LastSeenAt > now - ActiveWindow, ct));
        var share = Math.Min(Options.MaxAccountsPerWorker, (int)Math.Ceiling((double)enabled / workers));
        // Counted after the enabled accounts and only for enabled ones: an account paused in between (after the
        // release above) would otherwise count here but not there, and a mailbox would be handed back for nothing.
        var held = await mine.CountAsync(l => db.MailAccounts.Any(a => a.Id == l.AccountId && a.Enabled), ct);

        if (held < share)
        {
            var free = await db.MailboxLeases
                .Where(l => (l.WorkerId == null || l.ExpiresAt < now) && db.MailAccounts.Any(a => a.Id == l.AccountId && a.Enabled))
                .OrderBy(l => l.AccountId)
                .Select(l => l.AccountId)
                .Take(share - held)
                .ToListAsync(ct);
            foreach (var accountId in free)
            {
                // Only if nobody took it meanwhile.
                await db.MailboxLeases
                    .Where(l => l.AccountId == accountId && (l.WorkerId == null || l.ExpiresAt < now))
                    .ExecuteUpdateAsync(u => u.SetProperty(l => l.WorkerId, me.Id).SetProperty(l => l.ExpiresAt, until).SetProperty(l => l.AcquiredAt, now), ct);
            }
        }
        else if (held > share)
        {
            // More than a fair share (e.g. another worker started): hand one back per pass, gently. The share is
            // rounded up, so workers with an uneven split don't pass mailboxes back and forth.
            var surplus = await mine.OrderByDescending(l => l.AcquiredAt).Select(l => l.AccountId).FirstAsync(ct);
            await db.MailboxLeases.Where(l => l.AccountId == surplus && l.WorkerId == me.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.WorkerId, (string?)null), ct);
        }

        return await mine
            .Join(db.MailAccounts.Where(a => a.Enabled), l => l.AccountId, a => a.Id, (l, a) => new { a.Id, a.UpdatedAt })
            .ToDictionaryAsync(x => x.Id, x => x.UpdatedAt, ct);
    }

    /// <summary>Gives every lease back right away (on shutdown), so other workers need not wait for expiry.</summary>
    public async Task ReleaseAllAsync(CancellationToken ct)
    {
        await db.MailboxLeases.Where(l => l.WorkerId == me.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.WorkerId, (string?)null).SetProperty(l => l.ExpiresAt, time.GetUtcNow()), ct);
        await db.Workers.Where(w => w.Id == me.Id).ExecuteDeleteAsync(ct);
    }

    private async Task HeartbeatAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Workers.FindAsync([me.Id], ct) is { } self)
        {
            self.LastSeenAt = now;
        }
        else
        {
            db.Workers.Add(new WorkerInstance { Id = me.Id, Host = me.Host, StartedAt = now, LastSeenAt = now });
        }

        await db.SaveChangesAsync(ct);

        // Workers that died without saying goodbye.
        await db.Workers.Where(w => w.LastSeenAt < now - TimeSpan.FromHours(1)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Every enabled account gets a (free) lease row; several workers may try at once.</summary>
    private async Task EnsureLeaseRowsAsync(CancellationToken ct)
    {
        var missing = await db.MailAccounts
            .Where(a => a.Enabled && !db.MailboxLeases.Any(l => l.AccountId == a.Id))
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (missing.Count == 0)
        {
            return;
        }

        db.MailboxLeases.AddRange(missing.Select(id => new MailboxLease { AccountId = id, ExpiresAt = DateTimeOffset.UnixEpoch }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another worker inserted some of them first; the next pass sees them.
            db.ChangeTracker.Clear();
        }
    }
}
