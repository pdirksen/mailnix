using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

/// <summary>
/// Permanently removes messages that have been in the account's Trash for longer than it allows (opt-in). The
/// server does not record when a message was deleted, so the clock starts when mailnix first sees it in Trash.
/// </summary>
internal sealed partial class TrashPurger(
    MailnixDbContext db,
    StatsRecorder stats,
    IOptions<WorkerOptions> options,
    TimeProvider time,
    ILogger<TrashPurger> logger)
{
    public async Task PurgeAsync(IMailboxSession session, MailAccount account, RuleRun run, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (account.PurgeTrashAfterDays is not { } days || account.SpecialFolders.Trash is not { } trash)
        {
            Finish(run, "Purging Trash is switched off for this account; nothing was removed.", failed: false);
            return;
        }

        // The setting may have changed since the run was queued.
        run.Folder = trash;
        if (trash.Equals("INBOX", StringComparison.OrdinalIgnoreCase) || account.WatchedFolders.Contains(trash))
        {
            Finish(run, $"The Trash folder is set to \"{trash}\", which rules watch; nothing was removed. Choose the real Trash folder in the account settings.", failed: true);
            return;
        }

        if (!session.Capabilities.UidPlus)
        {
            Finish(run, "The mail server cannot remove single messages (UIDPLUS); Trash was not purged.", failed: true);
            return;
        }

        var status = await session.OpenAsync(trash, writable: false, ct);
        var uids = (await session.SearchAsync(trash, null, ct)).Uids;
        var present = uids.ToHashSet();

        // Forget what is gone, what belonged to another Trash folder or to an earlier incarnation of this one.
        var observed = await db.TrashObservations.Where(t => t.AccountId == account.Id).ToListAsync(ct);
        db.TrashObservations.RemoveRange(observed.Where(t => t.Folder != trash || t.UidValidity != status.UidValidity || !present.Contains(t.Uid)));
        var known = observed.Where(t => t.Folder == trash && t.UidValidity == status.UidValidity && present.Contains(t.Uid)).ToDictionary(t => t.Uid);
        foreach (var uid in uids.Where(u => !known.ContainsKey(u)))
        {
            db.TrashObservations.Add(new TrashObservation { AccountId = account.Id, Folder = trash, UidValidity = status.UidValidity, Uid = uid, FirstSeenAt = now });
        }

        var cutoff = now - TimeSpan.FromDays(days);
        var due = known.Values.Where(t => t.FirstSeenAt <= cutoff).Select(t => t.Uid).Order().Take(Options.MaxPurgePerRun).ToList();
        run.MessagesExamined = uids.Count;
        run.MessagesMatched = due.Count;
        run.PlannedActions = due.Count;

        if (due.Count > 0)
        {
            var messages = new List<MessageSnapshot>();
            foreach (var batch in due.Chunk(Options.FetchBatchSize))
            {
                messages.AddRange(await session.FetchAsync(trash, batch, FetchRequirements.None, ct));
            }

            if (!await session.PurgeAsync(trash, [.. messages.Select(m => m.Uid)], ct))
            {
                Finish(run, "The mail server cannot remove single messages (UIDPLUS); Trash was not purged.", failed: true);
                await db.SaveChangesAsync(ct);
                return;
            }

            var entries = messages.Select(m => Entry(run, account, trash, status.UidValidity, m, now)).ToList();
            db.ActionLog.AddRange(entries);
            await stats.ActionsAsync(account, entries, ct);

            db.TrashObservations.RemoveRange(messages.Select(m => known[m.Uid]));
            run.ExecutedActions = messages.Count;
            LogPurged(messages.Count, account.Id);
        }

        Finish(run, null, failed: false);
        await db.SaveChangesAsync(ct);
    }

    private void Finish(RuleRun run, string? error, bool failed)
    {
        run.State = failed ? RunState.Failed : RunState.Completed;
        run.Error = error;
        run.FinishedAt = time.GetUtcNow();
    }

    private static ActionLogEntry Entry(RuleRun run, MailAccount account, string folder, uint uidValidity, MessageSnapshot message, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        RunId = run.Id,
        AccountId = account.Id,
        UserId = account.UserId,
        Action = ActionType.Purge,
        SourceFolder = folder,
        SourceUid = message.Uid,
        SourceUidValidity = uidValidity,
        MessageId = message.MessageId,
        From = message.From?.Address,
        Subject = message.Subject is { Length: > 300 } subject ? subject[..300] : message.Subject,
        Success = true,
        CreatedAt = now,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} messages from the Trash of account {AccountId}")]
    private partial void LogPurged(int count, Guid accountId);

    private WorkerOptions Options => options.Value;
}
