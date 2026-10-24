using MailKit;
using MailKit.Net.Imap;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Worker;

/// <summary>
/// Reverts logged actions: moves messages back, removes copies and restores flags and keywords.
/// Messages that can no longer be found are reported, never guessed.
/// </summary>
internal sealed class UndoProcessor(MailnixDbContext db, TimeProvider time)
{
    public async Task UndoAsync(IMailboxSession session, MailAccount account, RuleRun run, CancellationToken ct)
    {
        IQueryable<ActionLogEntry> query = db.ActionLog.Where(a => a.AccountId == account.Id && a.Success && !a.DryRun && a.UndoneAt == null);
        List<ActionLogEntry> entries;
        if (run.UndoRunId is { } runId)
        {
            entries = await query.Where(a => a.RunId == runId).ToListAsync(ct);
        }
        else
        {
            entries = await query.Where(a => a.RuleId == run.UndoRuleId).OrderByDescending(a => a.CreatedAt).Take(run.UndoCount ?? 10).ToListAsync(ct);
        }

        var errors = new List<string>();

        // Newest first, one message (of one run) at a time.
        foreach (var message in entries.GroupBy(e => (e.RunId, e.SourceFolder, e.SourceUid, e.SourceUidValidity)).OrderByDescending(g => g.Max(e => e.CreatedAt)))
        {
            var group = message.ToList();
            try
            {
                await UndoMessageAsync(session, account, run, group, errors, ct);
            }
            catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException or Mail.FolderNotFoundException or InvalidOperationException)
            {
                errors.Add($"{Describe(group[0])}: {ex.Message}");
            }
        }

        var undone = entries.Count(e => e.UndoneAt is not null);
        run.MessagesExamined = entries.Select(e => (e.RunId, e.SourceUid)).Distinct().Count();
        run.PlannedActions = entries.Count;
        run.ExecutedActions = undone;
        run.FailedActions = entries.Count - undone;
        run.Error = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Take(10));
        run.State = run.FailedActions > 0 ? RunState.CompletedWithErrors : RunState.Completed;
        run.FinishedAt = time.GetUtcNow();

        if (run.UndoRunId is { } undoneRunId && await db.Runs.FindAsync([undoneRunId], ct) is { } original && run.FailedActions == 0)
        {
            original.UndoneByRunId = run.Id;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UndoMessageAsync(IMailboxSession session, MailAccount account, RuleRun run, List<ActionLogEntry> entries, List<string> errors, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var first = entries[0];
        string location = first.SourceFolder;
        uint? uid;

        // 1. Move it back where it came from.
        if (entries.FirstOrDefault(e => RunProcessor.IsTerminal(e.Action)) is { } move)
        {
            var found = await session.LocateAsync(move.TargetFolder!, move.TargetUidValidity ?? 0, move.TargetUid ?? 0, move.MessageId, ct);
            if (found is null)
            {
                errors.Add($"{Describe(move)}: not found in {move.TargetFolder} any more.");
                return;
            }

            uid = await session.MoveAsync(move.TargetFolder!, found.Value, move.SourceFolder, ct)
                ?? await session.LocateAsync(move.SourceFolder, 0, 0, move.MessageId, ct);
            move.UndoneAt = now;

            // Record where the message went back to: the watcher's loop guard then leaves it alone
            // instead of applying the same rule to it again (which would undo the undo).
            var source = await session.OpenAsync(move.SourceFolder, writable: true, ct);
            db.ActionLog.Add(new ActionLogEntry
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                AccountId = account.Id,
                UserId = account.UserId,
                Action = ActionType.MoveTo,
                SourceFolder = move.TargetFolder!,
                SourceUid = found.Value,
                SourceUidValidity = move.TargetUidValidity ?? 0,
                TargetFolder = move.SourceFolder,
                TargetUid = uid,
                TargetUidValidity = source.UidValidity,
                MessageId = move.MessageId,
                From = move.From,
                Subject = move.Subject,
                Success = true,
                CreatedAt = now,
                UndoneAt = now, // not undoable itself
            });
        }
        else
        {
            uid = await session.LocateAsync(first.SourceFolder, first.SourceUidValidity, first.SourceUid, first.MessageId, ct);
        }

        // 2. Remove copies made by the run.
        foreach (var copy in entries.Where(e => e.Action == ActionType.CopyTo))
        {
            var found = await session.LocateAsync(copy.TargetFolder!, copy.TargetUidValidity ?? 0, copy.TargetUid ?? 0, copy.MessageId, ct);
            if (found is null)
            {
                copy.UndoneAt = now; // nothing left to remove
            }
            else if (await session.ExpungeAsync(copy.TargetFolder!, found.Value, ct))
            {
                copy.UndoneAt = now;
            }
            else
            {
                errors.Add($"{Describe(copy)}: the server cannot remove a single copy; remove it from {copy.TargetFolder} manually.");
            }
        }

        // 3. Restore flags and keywords as they were before the run.
        var flagEntries = entries.Where(e => e.Action is ActionType.MarkRead or ActionType.MarkUnread or ActionType.Flag or ActionType.Unflag
            or ActionType.AddKeyword or ActionType.RemoveKeyword).ToList();
        if (flagEntries.Count == 0)
        {
            return;
        }

        if (uid is null)
        {
            errors.Add($"{Describe(first)}: not found in {location} any more.");
            return;
        }

        bool Had(ActionLogEntry e, string flag) => e.PreviousFlags.Contains(flag, StringComparer.OrdinalIgnoreCase);
        bool? seen = null, flagged = null;
        var add = new List<string>();
        var remove = new List<string>();
        foreach (var entry in flagEntries)
        {
            switch (entry.Action)
            {
                case ActionType.MarkRead or ActionType.MarkUnread:
                    seen = Had(entry, Flags.Seen);
                    break;
                case ActionType.Flag or ActionType.Unflag:
                    flagged = Had(entry, Flags.Flagged);
                    break;
                case ActionType.AddKeyword when !Had(entry, entry.Keyword!):
                    remove.Add(entry.Keyword!);
                    break;
                case ActionType.RemoveKeyword when Had(entry, entry.Keyword!):
                    add.Add(entry.Keyword!);
                    break;
            }
        }

        await session.SetFlagsAsync(location, uid.Value, seen, flagged, add, remove, ct);
        foreach (var entry in flagEntries)
        {
            entry.UndoneAt = now;
        }
    }

    private static string Describe(ActionLogEntry entry) => $"\"{entry.Subject ?? entry.MessageId ?? entry.SourceUid.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";
}
