using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Core.Tests.Search;
using Mailnix.Mail;
using MimeKit;

namespace Mailnix.Integration.Tests.Fakes;

/// <summary>An in-memory mailbox standing in for an IMAP server in worker tests.</summary>
internal sealed class FakeMailServer : IMailboxSessionFactory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Folder> _folders = new(StringComparer.Ordinal) { ["INBOX"] = new() };

    public char Delimiter { get; init; } = '.';

    /// <summary>When set, connecting fails with this error.</summary>
    public MailConnectionException? ConnectError { get; set; }

    public int Connections { get; private set; }

    /// <summary>Servers without UIDPLUS cannot expunge single messages.</summary>
    public bool UidPlus { get; set; } = true;

    /// <summary>Every search a session ran (null = all messages), in order.</summary>
    public List<SearchFilter?> Searches { get; } = [];

    public Task<IMailboxSession> OpenAsync(ImapConnectionSettings settings, CancellationToken ct)
    {
        lock (_lock)
        {
            if (ConnectError is { } error)
            {
                throw error;
            }

            Connections++;
        }

        return Task.FromResult<IMailboxSession>(new Session(this));
    }

    public uint Append(string folder, MessageSnapshot message)
    {
        lock (_lock)
        {
            var target = GetOrCreate(folder);
            var uid = target.NextUid++;
            target.Messages.Add(new Stored(uid, message with { Uid = uid, Folder = folder }));
            return uid;
        }
    }

    public void CreateFolder(string folder)
    {
        lock (_lock)
        {
            GetOrCreate(folder);
        }
    }

    public IReadOnlyList<MessageSnapshot> Messages(string folder)
    {
        lock (_lock)
        {
            return _folders.TryGetValue(folder, out var f) ? [.. f.Messages.Select(m => m.Snapshot)] : [];
        }
    }

    public bool HasFolder(string folder)
    {
        lock (_lock)
        {
            return _folders.ContainsKey(folder);
        }
    }

    private Folder GetOrCreate(string path) => _folders.TryGetValue(path, out var folder) ? folder : _folders[path] = new Folder();

    private Folder Get(string path) => _folders.TryGetValue(path, out var folder) ? folder : throw new Mail.FolderNotFoundException(path);

    private sealed class Folder
    {
        public uint UidValidity { get; } = (uint)Random.Shared.Next(1, int.MaxValue);

        public uint NextUid { get; set; } = 1;

        public List<Stored> Messages { get; } = [];
    }

    private sealed class Stored(uint uid, MessageSnapshot snapshot)
    {
        public uint Uid => uid;

        public MessageSnapshot Snapshot { get; set; } = snapshot;
    }

    private sealed class Session(FakeMailServer server) : IMailboxSession
    {
        public MailboxCapabilities Capabilities { get; } = new(true, server.UidPlus, true, false, true, true, false, false, ["Idle", "Move", .. server.UidPlus ? new[] { "UidPlus" } : []]);

        public char Delimiter => server.Delimiter;

        public Task<FolderStatus> OpenAsync(string folder, bool writable, CancellationToken ct)
        {
            lock (server._lock)
            {
                var f = server.Get(folder);
                return Task.FromResult(new FolderStatus(folder, f.UidValidity, f.NextUid, f.Messages.Count));
            }
        }

        public Task<IReadOnlyList<MessageSnapshot>> FetchNewAsync(string folder, uint afterUid, int max, FetchRequirements requirements, CancellationToken ct) =>
            Read(folder, f => f.Messages.Where(m => m.Uid > afterUid).OrderBy(m => m.Uid).Take(max));

        public Task<IReadOnlyList<MessageSnapshot>> FetchLatestAsync(string folder, int limit, FetchRequirements requirements, CancellationToken ct) =>
            Read(folder, f => f.Messages.OrderByDescending(m => m.Uid).Take(limit));

        public Task<SearchResult> SearchAsync(string folder, SearchFilter? filter, CancellationToken ct)
        {
            lock (server._lock)
            {
                server.Searches.Add(filter);
                var uids = server.Get(folder).Messages.Where(m => ImapSearchModel.Matches(filter, m.Snapshot)).Select(m => m.Uid).Order().ToList();
                return Task.FromResult(new SearchResult(uids, filter is not null));
            }
        }

        public Task<IReadOnlyList<MessageSnapshot>> FetchAsync(string folder, IReadOnlyList<uint> uids, FetchRequirements requirements, CancellationToken ct) =>
            Read(folder, f => f.Messages.Where(m => uids.Contains(m.Uid)));

        public Task<IReadOnlyList<ActionOutcome>> ExecuteAsync(string folder, IReadOnlyList<MessageActions> work, CancellationToken ct)
        {
            lock (server._lock)
            {
                var source = server.Get(folder);
                var outcomes = new List<ActionOutcome>();
                foreach (var (message, actions) in work)
                {
                    var stored = source.Messages.Single(m => m.Uid == message.Uid);
                    foreach (var action in actions.OrderBy(a => a.IsTerminal))
                    {
                        var s = stored.Snapshot;
                        switch (action.Type)
                        {
                            case ActionType.MarkRead or ActionType.MarkUnread:
                                stored.Snapshot = s with { IsSeen = action.Type == ActionType.MarkRead };
                                break;
                            case ActionType.Flag or ActionType.Unflag:
                                stored.Snapshot = s with { IsFlagged = action.Type == ActionType.Flag };
                                break;
                            case ActionType.AddKeyword:
                                stored.Snapshot = s with { Keywords = [.. s.Keywords.Union([action.Keyword!])] };
                                break;
                            case ActionType.RemoveKeyword:
                                stored.Snapshot = s with { Keywords = [.. s.Keywords.Except([action.Keyword!])] };
                                break;
                            case ActionType.CopyTo:
                                var copyTarget = server.GetOrCreate(action.TargetFolder!);
                                outcomes.Add(new ActionOutcome(message.Uid, action, true, TargetUid: Add(copyTarget, action.TargetFolder!, s), TargetUidValidity: copyTarget.UidValidity));
                                continue;
                            default:
                                var moveTarget = server.GetOrCreate(action.TargetFolder!);
                                source.Messages.Remove(stored);
                                outcomes.Add(new ActionOutcome(message.Uid, action, true, TargetUid: Add(moveTarget, action.TargetFolder!, s), TargetUidValidity: moveTarget.UidValidity));
                                continue;
                        }

                        outcomes.Add(new ActionOutcome(message.Uid, action, true));
                    }
                }

                return Task.FromResult<IReadOnlyList<ActionOutcome>>(outcomes);
            }
        }

        public async Task<IdleOutcome> IdleAsync(string folder, TimeSpan timeout, CancellationToken wake, CancellationToken ct)
        {
            var start = Count(folder);
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (wake.IsCancellationRequested)
                {
                    return IdleOutcome.Woken;
                }

                if (Count(folder) > start)
                {
                    return IdleOutcome.NewMessages;
                }

                await Task.Delay(25, ct);
            }

            return IdleOutcome.TimedOut;
        }

        public Task<IReadOnlySet<string>> MessageIdsAsync(string folder, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(server.Messages(folder).Select(m => m.MessageId).OfType<string>().ToHashSet());

        public Task<uint?> LocateAsync(string folder, uint uidValidity, uint uid, string? messageId, CancellationToken ct)
        {
            lock (server._lock)
            {
                var f = server.Get(folder);
                if (f.UidValidity == uidValidity && f.Messages.Any(m => m.Uid == uid))
                {
                    return Task.FromResult<uint?>(uid);
                }

                return Task.FromResult(f.Messages.LastOrDefault(m => messageId is not null && m.Snapshot.MessageId == messageId)?.Uid);
            }
        }

        public Task<uint?> MoveAsync(string folder, uint uid, string target, CancellationToken ct)
        {
            lock (server._lock)
            {
                var source = server.Get(folder);
                var stored = source.Messages.Single(m => m.Uid == uid);
                source.Messages.Remove(stored);
                return Task.FromResult<uint?>(Add(server.GetOrCreate(target), target, stored.Snapshot));
            }
        }

        public Task SetFlagsAsync(string folder, uint uid, bool? seen, bool? flagged, IReadOnlyCollection<string> addKeywords, IReadOnlyCollection<string> removeKeywords, CancellationToken ct)
        {
            lock (server._lock)
            {
                var stored = server.Get(folder).Messages.Single(m => m.Uid == uid);
                var s = stored.Snapshot;
                stored.Snapshot = s with
                {
                    IsSeen = seen ?? s.IsSeen,
                    IsFlagged = flagged ?? s.IsFlagged,
                    Keywords = [.. s.Keywords.Union(addKeywords).Except(removeKeywords)],
                };
                return Task.CompletedTask;
            }
        }

        public Task<bool> ExpungeAsync(string folder, uint uid, CancellationToken ct)
        {
            lock (server._lock)
            {
                server.Get(folder).Messages.RemoveAll(m => m.Uid == uid);
                return Task.FromResult(true);
            }
        }

        public Task<bool> PurgeAsync(string folder, IReadOnlyList<uint> uids, CancellationToken ct)
        {
            lock (server._lock)
            {
                if (!server.UidPlus)
                {
                    return Task.FromResult(false);
                }

                server.Get(folder).Messages.RemoveAll(m => uids.Contains(m.Uid));
                return Task.FromResult(true);
            }
        }

        public Task<uint?> AppendAsync(string folder, MimeMessage message, CancellationToken ct) =>
            Task.FromResult<uint?>(server.Append(folder, MessageSnapshotFactory.FromMimeMessage(message)));

        public Task CreateFolderAsync(string path, CancellationToken ct)
        {
            lock (server._lock)
            {
                server.GetOrCreate(path);
                return Task.CompletedTask;
            }
        }

        public Task DeleteFolderAsync(string path, CancellationToken ct)
        {
            lock (server._lock)
            {
                server._folders.Remove(path);
                return Task.CompletedTask;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private int Count(string folder)
        {
            lock (server._lock)
            {
                return server.Get(folder).Messages.Count;
            }
        }

        private Task<IReadOnlyList<MessageSnapshot>> Read(string folder, Func<Folder, IEnumerable<Stored>> select)
        {
            lock (server._lock)
            {
                return Task.FromResult<IReadOnlyList<MessageSnapshot>>([.. select(server.Get(folder)).Select(m => m.Snapshot)]);
            }
        }

        private static uint Add(Folder folder, string path, MessageSnapshot snapshot)
        {
            var uid = folder.NextUid++;
            folder.Messages.Add(new Stored(uid, snapshot with { Uid = uid, Folder = path }));
            return uid;
        }
    }
}
