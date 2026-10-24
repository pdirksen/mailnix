using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using MimeKit;

namespace Mailnix.Mail;

internal sealed class ImapMailboxSessionFactory(IImapConnector connector) : IMailboxSessionFactory
{
    public async Task<IMailboxSession> OpenAsync(ImapConnectionSettings settings, CancellationToken ct)
    {
        var client = await connector.ConnectAsync(settings, ct);
        return new ImapMailboxSession(client);
    }
}

internal sealed class ImapMailboxSession(ImapClient client) : IMailboxSession
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private IMailFolder? _open;
    private FolderAccess _access;

    public MailboxCapabilities Capabilities { get; } = MailboxProbe.ReadCapabilities(client.Capabilities);

    public char Delimiter { get; } = (client.PersonalNamespaces.FirstOrDefault()?.DirectorySeparator) ?? client.Inbox.DirectorySeparator;

    public async Task<FolderStatus> OpenAsync(string folder, bool writable, CancellationToken ct)
    {
        // Always (re)select: UIDNEXT of an already selected folder is not updated by the server (e.g. after IDLE
        // reported new messages), and callers use it to decide whether there is anything new.
        var mailFolder = await GetOpenFolderAsync(folder, writable, ct, forceReopen: true);
        return new FolderStatus(mailFolder.FullName, mailFolder.UidValidity, mailFolder.UidNext?.Id ?? 0, mailFolder.Count);
    }

    public async Task<IReadOnlyList<MessageSnapshot>> FetchNewAsync(string folder, uint afterUid, int max, FetchRequirements requirements, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: false, ct);
        if (mailFolder.Count == 0 || mailFolder.UidNext?.Id is { } next && next <= afterUid + 1)
        {
            return [];
        }

        var range = new UniqueIdRange(new UniqueId(afterUid + 1), UniqueId.MaxValue);
        var summaries = await mailFolder.FetchAsync(range, Items(requirements), ct);
        var snapshots = new List<MessageSnapshot>();
        foreach (var summary in summaries.Where(s => s.UniqueId.Id > afterUid).OrderBy(s => s.UniqueId.Id).Take(max))
        {
            snapshots.Add(await ToSnapshotAsync(mailFolder, summary, requirements, ct));
        }

        return snapshots;
    }

    public async Task<IReadOnlyList<MessageSnapshot>> FetchLatestAsync(string folder, int limit, FetchRequirements requirements, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: false, ct);
        if (mailFolder.Count == 0)
        {
            return [];
        }

        var summaries = await mailFolder.FetchAsync(Math.Max(0, mailFolder.Count - limit), -1, Items(requirements), ct);
        var snapshots = new List<MessageSnapshot>();
        foreach (var summary in summaries.OrderByDescending(s => s.UniqueId.Id))
        {
            snapshots.Add(await ToSnapshotAsync(mailFolder, summary, requirements, ct));
        }

        return snapshots;
    }

    public async Task<SearchResult> SearchAsync(string folder, SearchFilter? filter, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: false, ct);
        if (mailFolder.Count == 0)
        {
            return new SearchResult([], filter is not null);
        }

        if (filter is not null)
        {
            try
            {
                return new SearchResult(Ascending(await mailFolder.SearchAsync(ToQuery(filter), ct)), true);
            }
            catch (ImapCommandException)
            {
                // The server refused the criteria (e.g. unsupported search key): fall back to every message.
            }
        }

        return new SearchResult(Ascending(await mailFolder.SearchAsync(SearchQuery.All, ct)), false);
    }

    public async Task<IReadOnlyList<MessageSnapshot>> FetchAsync(string folder, IReadOnlyList<uint> uids, FetchRequirements requirements, CancellationToken ct)
    {
        if (uids.Count == 0)
        {
            return [];
        }

        var mailFolder = await GetOpenFolderAsync(folder, writable: false, ct);
        var summaries = await mailFolder.FetchAsync(uids.Select(u => new UniqueId(u)).ToList(), Items(requirements), ct);
        var snapshots = new List<MessageSnapshot>();
        foreach (var summary in summaries)
        {
            snapshots.Add(await ToSnapshotAsync(mailFolder, summary, requirements, ct));
        }

        return snapshots;
    }

    public async Task<IReadOnlyList<ActionOutcome>> ExecuteAsync(string folder, IReadOnlyList<MessageActions> work, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: true, ct);
        var outcomes = new List<ActionOutcome>();
        var items = work.SelectMany(w => w.Actions.Select(a => (Uid: w.Message.Uid, Action: a))).ToList();

        // Flags and keywords first: after a move the UIDs in this folder are gone.
        await StoreAsync(ActionType.MarkRead, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true });
        await StoreAsync(ActionType.MarkUnread, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Seen) { Silent = true });
        await StoreAsync(ActionType.Flag, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Flagged) { Silent = true });
        await StoreAsync(ActionType.Unflag, new StoreFlagsRequest(StoreAction.Remove, MessageFlags.Flagged) { Silent = true });
        foreach (var group in items.Where(i => i.Action.Type is ActionType.AddKeyword or ActionType.RemoveKeyword).GroupBy(i => (i.Action.Type, i.Action.Keyword)))
        {
            var action = group.Key.Type == ActionType.AddKeyword ? StoreAction.Add : StoreAction.Remove;
            await RunBatchAsync([.. group], uids => mailFolder.StoreAsync(uids, new StoreFlagsRequest(action, [group.Key.Keyword!]) { Silent = true }, ct));
        }

        foreach (var group in items.Where(i => i.Action.Type == ActionType.CopyTo).GroupBy(i => i.Action.TargetFolder!))
        {
            await TransferAsync([.. group], group.Key, move: false);
        }

        foreach (var group in items.Where(i => i.Action.IsTerminal).GroupBy(i => i.Action.TargetFolder!))
        {
            await TransferAsync([.. group], group.Key, move: true);
        }

        return outcomes;

        Task StoreAsync(ActionType type, StoreFlagsRequest request)
        {
            var batch = items.Where(i => i.Action.Type == type).ToList();
            return batch.Count == 0 ? Task.CompletedTask : RunBatchAsync(batch, uids => mailFolder.StoreAsync(uids, request, ct));
        }

        async Task RunBatchAsync(List<(uint Uid, PlannedAction Action)> batch, Func<IList<UniqueId>, Task> operation)
        {
            try
            {
                await operation([.. batch.Select(b => new UniqueId(b.Uid))]);
                outcomes.AddRange(batch.Select(b => new ActionOutcome(b.Uid, b.Action, true)));
            }
            catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException or InvalidOperationException)
            {
                outcomes.AddRange(batch.Select(b => new ActionOutcome(b.Uid, b.Action, false, ex.Message)));
            }
        }

        async Task TransferAsync(List<(uint Uid, PlannedAction Action)> batch, string target, bool move)
        {
            try
            {
                var destination = await EnsureFolderAsync(target, ct);
                IList<UniqueId> uids = [.. batch.Select(b => new UniqueId(b.Uid))];
                var map = move
                    ? await mailFolder.MoveToAsync(uids, destination, ct)
                    : await mailFolder.CopyToAsync(uids, destination, ct);
                foreach (var (uid, action) in batch)
                {
                    var mapped = map.TryGetValue(new UniqueId(uid), out var destinationUid) ? destinationUid : (UniqueId?)null;
                    outcomes.Add(new ActionOutcome(uid, action, true, TargetUid: mapped?.Id, TargetUidValidity: mapped?.Validity is > 0 ? mapped.Value.Validity : null));
                }
            }
            catch (Exception ex) when (ex is ImapCommandException or ImapProtocolException or FolderNotFoundException or MailKit.FolderNotFoundException or InvalidOperationException)
            {
                outcomes.AddRange(batch.Select(b => new ActionOutcome(b.Uid, b.Action, false, ex.Message)));
            }
        }
    }

    public async Task<IdleOutcome> IdleAsync(string folder, TimeSpan timeout, CancellationToken wake, CancellationToken ct)
    {
        // Waiting needs no write access (EXAMINE), so watching a folder never changes it.
        var mailFolder = await GetOpenFolderAsync(folder, writable: false, ct);
        var startCount = mailFolder.Count;
        using var done = CancellationTokenSource.CreateLinkedTokenSource(wake);
        done.CancelAfter(timeout);
        var arrived = false;

        void OnCountChanged(object? sender, EventArgs e)
        {
            if (mailFolder.Count > startCount)
            {
                arrived = true;
                done.Cancel();
            }
        }

        mailFolder.CountChanged += OnCountChanged;
        try
        {
            if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
            {
                await client.IdleAsync(done.Token, ct);
            }
            else
            {
                while (!done.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, done.Token).ContinueWith(_ => { }, ct, TaskContinuationOptions.None, TaskScheduler.Default);
                    await client.NoOpAsync(ct);
                }
            }
        }
        finally
        {
            mailFolder.CountChanged -= OnCountChanged;
        }

        return arrived ? IdleOutcome.NewMessages : wake.IsCancellationRequested ? IdleOutcome.Woken : IdleOutcome.TimedOut;
    }

    public async Task<IReadOnlySet<string>> MessageIdsAsync(string folder, int limit, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: false, ct);
        if (mailFolder.Count == 0)
        {
            return new HashSet<string>();
        }

        var summaries = await mailFolder.FetchAsync(Math.Max(0, mailFolder.Count - limit), -1, MessageSummaryItems.Envelope, ct);
        return summaries.Select(s => s.Envelope?.MessageId).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    public async Task<uint?> LocateAsync(string folder, uint uidValidity, uint uid, string? messageId, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: true, ct);
        if (mailFolder.UidValidity == uidValidity && uid > 0)
        {
            var found = await mailFolder.SearchAsync(SearchQuery.Uids([new UniqueId(uid)]), ct);
            if (found.Count == 1)
            {
                return uid;
            }
        }

        if (string.IsNullOrEmpty(messageId))
        {
            return null;
        }

        var byId = await mailFolder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), ct);
        return byId.Count > 0 ? byId[^1].Id : null;
    }

    public async Task<uint?> MoveAsync(string folder, uint uid, string target, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: true, ct);
        var destination = await EnsureFolderAsync(target, ct);
        var moved = await mailFolder.MoveToAsync(new UniqueId(uid), destination, ct);
        return moved?.Id;
    }

    public async Task SetFlagsAsync(string folder, uint uid, bool? seen, bool? flagged, IReadOnlyCollection<string> addKeywords, IReadOnlyCollection<string> removeKeywords, CancellationToken ct)
    {
        var mailFolder = await GetOpenFolderAsync(folder, writable: true, ct);
        IList<UniqueId> uids = [new UniqueId(uid)];
        var add = (seen == true ? MessageFlags.Seen : MessageFlags.None) | (flagged == true ? MessageFlags.Flagged : MessageFlags.None);
        var remove = (seen == false ? MessageFlags.Seen : MessageFlags.None) | (flagged == false ? MessageFlags.Flagged : MessageFlags.None);
        if (add != MessageFlags.None || addKeywords.Count > 0)
        {
            await mailFolder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, add, addKeywords.ToHashSet()) { Silent = true }, ct);
        }

        if (remove != MessageFlags.None || removeKeywords.Count > 0)
        {
            await mailFolder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Remove, remove, removeKeywords.ToHashSet()) { Silent = true }, ct);
        }
    }

    public async Task<bool> ExpungeAsync(string folder, uint uid, CancellationToken ct)
    {
        // Without UIDPLUS, EXPUNGE would also remove other messages already marked \Deleted.
        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            return false;
        }

        var mailFolder = await GetOpenFolderAsync(folder, writable: true, ct);
        IList<UniqueId> uids = [new UniqueId(uid)];
        await mailFolder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
        await mailFolder.ExpungeAsync(uids, ct);
        return true;
    }

    public async Task<bool> PurgeAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            return false;
        }

        var mailFolder = await GetOpenFolderAsync(folder, writable: true, ct);
        foreach (var batch in uids.Chunk(500))
        {
            IList<UniqueId> set = [.. batch.Select(u => new UniqueId(u))];
            await mailFolder.StoreAsync(set, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
            await mailFolder.ExpungeAsync(set, ct);
        }

        return true;
    }

    public async Task<uint?> AppendAsync(string folder, MimeMessage message, CancellationToken ct)
    {
        var mailFolder = await EnsureFolderAsync(folder, ct);
        var uid = await mailFolder.AppendAsync(new AppendRequest(message), ct);
        return uid?.Id;
    }

    public async Task CreateFolderAsync(string path, CancellationToken ct) => await EnsureFolderAsync(path, ct);

    public async Task DeleteFolderAsync(string path, CancellationToken ct)
    {
        IMailFolder folder;
        try
        {
            folder = await client.GetFolderAsync(path, ct);
        }
        catch (MailKit.FolderNotFoundException)
        {
            return;
        }

        foreach (var child in await folder.GetSubfoldersAsync(false, ct))
        {
            await DeleteFolderAsync(child.FullName, ct);
        }

        if (_open?.FullName == folder.FullName)
        {
            await _open.CloseAsync(false, ct);
            _open = null;
        }

        await folder.DeleteAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(quit: true, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or ImapProtocolException or OperationCanceledException or ServiceNotConnectedException)
        {
            // Best effort.
        }

        client.Dispose();
    }

    private static MessageSummaryItems Items(FetchRequirements requirements)
    {
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate | MessageSummaryItems.Size;
        if (requirements.HasFlag(FetchRequirements.Headers))
        {
            items |= MessageSummaryItems.Headers;
        }

        if (requirements.HasFlag(FetchRequirements.BodyStructure) || requirements.HasFlag(FetchRequirements.Body))
        {
            items |= MessageSummaryItems.BodyStructure;
        }

        return items;
    }

    private static async Task<MessageSnapshot> ToSnapshotAsync(IMailFolder folder, IMessageSummary summary, FetchRequirements requirements, CancellationToken ct)
    {
        string? body = null;
        if (requirements.HasFlag(FetchRequirements.Body) && (summary.TextBody ?? summary.HtmlBody) is { } part
            && await folder.GetBodyPartAsync(summary.UniqueId, part, ct) is TextPart text)
        {
            body = text.IsHtml ? MessageSnapshotFactory.HtmlToText(text.Text) : text.Text;
            if (body is { Length: > MessageReader.MaxBodyLength })
            {
                body = body[..MessageReader.MaxBodyLength];
            }
        }

        return MessageReader.ToSnapshot(summary, folder.FullName, requirements, body);
    }

    private static List<uint> Ascending(IList<UniqueId> uids) => [.. uids.Select(u => u.Id).Order()];

    internal static SearchQuery ToQuery(SearchFilter filter) => filter switch
    {
        SearchAnd and => and.Children.Select(ToQuery).Aggregate(SearchQuery.And),
        SearchOr or => or.Children.Select(ToQuery).Aggregate(SearchQuery.Or),
        SearchNot not => SearchQuery.Not(ToQuery(not.Inner)),
        SearchText { Field: SearchTextField.From } text => SearchQuery.FromContains(text.Value),
        SearchText { Field: SearchTextField.To } text => SearchQuery.ToContains(text.Value),
        SearchText { Field: SearchTextField.Cc } text => SearchQuery.CcContains(text.Value),
        SearchText { Field: SearchTextField.Subject } text => SearchQuery.SubjectContains(text.Value),
        SearchText text => SearchQuery.HeaderContains(text.HeaderName!, text.Value),
        SearchFlagIs { Flag: SearchFlagKind.Seen } flag => flag.Set ? SearchQuery.Seen : SearchQuery.NotSeen,
        SearchFlagIs { Flag: SearchFlagKind.Flagged } flag => flag.Set ? SearchQuery.Flagged : SearchQuery.NotFlagged,
        SearchFlagIs { Flag: SearchFlagKind.Answered } flag => flag.Set ? SearchQuery.Answered : SearchQuery.NotAnswered,
        SearchKeyword keyword => SearchQuery.HasKeyword(keyword.Keyword),
        SearchLarger larger => SearchQuery.LargerThan((int)Math.Clamp(larger.Bytes, 0, int.MaxValue)),
        SearchSmaller smaller => SearchQuery.SmallerThan((int)Math.Clamp(smaller.Bytes, 1, int.MaxValue)),
        SearchBefore before => SearchQuery.DeliveredBefore(before.Day.ToDateTime(TimeOnly.MinValue)),
        SearchSince since => SearchQuery.DeliveredAfter(since.Day.ToDateTime(TimeOnly.MinValue)),
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown search filter."),
    };

    /// <summary>Keeps one folder open; reopens when a different folder or stronger access is needed.</summary>
    private async Task<IMailFolder> GetOpenFolderAsync(string folder, bool writable, CancellationToken ct, bool forceReopen = false)
    {
        var wanted = writable ? FolderAccess.ReadWrite : FolderAccess.ReadOnly;
        if (!forceReopen && _open is { IsOpen: true } open && open.FullName.Equals(folder, StringComparison.Ordinal) && (_access == FolderAccess.ReadWrite || !writable))
        {
            return open;
        }

        IMailFolder mailFolder;
        try
        {
            mailFolder = folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(folder, ct);
        }
        catch (MailKit.FolderNotFoundException)
        {
            throw new FolderNotFoundException(folder);
        }

        await mailFolder.OpenAsync(wanted, ct);
        (_open, _access) = (mailFolder, wanted);
        return mailFolder;
    }

    /// <summary>Returns the folder, creating it (and missing parents) if needed. New folders are subscribed.</summary>
    private async Task<IMailFolder> EnsureFolderAsync(string path, CancellationToken ct)
    {
        try
        {
            return path.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(path, ct);
        }
        catch (MailKit.FolderNotFoundException)
        {
            var cut = path.LastIndexOf(Delimiter);
            var parent = cut > 0
                ? await EnsureFolderAsync(path[..cut], ct)
                : client.GetFolder(client.PersonalNamespaces.FirstOrDefault() ?? new FolderNamespace(Delimiter, string.Empty));
            var created = await parent.CreateAsync(path[(cut + 1)..], isMessageFolder: true, ct)
                ?? throw new InvalidOperationException($"The server did not create folder '{path}'.");
            await created.SubscribeAsync(ct);
            return created;
        }
    }
}
