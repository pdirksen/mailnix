using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Mail;

namespace Mailnix.Integration.Tests.Fakes;

/// <summary>Serves prepared messages instead of reading a mail server.</summary>
internal sealed class FakeMessageReader : IMessageReader
{
    public Dictionary<string, List<MessageSnapshot>> Folders { get; } = new(StringComparer.Ordinal) { ["INBOX"] = [] };

    public HashSet<string> SentMessageIds { get; } = [];

    public List<(string Folder, int Limit, FetchRequirements Requirements)> Reads { get; } = [];

    public Task<FolderSample> ReadLatestAsync(ImapConnectionSettings settings, string folder, int limit, FetchRequirements requirements, CancellationToken ct)
    {
        Reads.Add((folder, limit, requirements));
        if (!Folders.TryGetValue(folder, out var messages))
        {
            throw new Mail.FolderNotFoundException(folder);
        }

        var latest = messages.OrderByDescending(m => m.Uid).Take(limit)
            .Select(m => m with { Folder = folder, Headers = requirements.HasFlag(FetchRequirements.Headers) ? m.Headers ?? [] : null })
            .ToList();
        return Task.FromResult(new FolderSample(folder, messages.Count, latest));
    }

    public Task<IReadOnlySet<string>> ReadMessageIdsAsync(ImapConnectionSettings settings, string folder, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(SentMessageIds);

    public static MessageSnapshot Message(uint uid, string from, string subject, bool newsletter = false, bool seen = false) => new()
    {
        Uid = uid,
        From = new EmailAddress(from),
        To = [new EmailAddress("owner@example.com")],
        Subject = subject,
        ReceivedAt = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero).AddHours(uid),
        Size = 4096,
        IsSeen = seen,
        Headers = newsletter ? [new("List-Unsubscribe", "<mailto:unsubscribe@example.com>")] : [],
    };
}
