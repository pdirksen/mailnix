using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.Extensions.Caching.Memory;

namespace Mailnix.Api.Rules;

internal sealed record MessageSample(FolderSample Sample, DateTimeOffset FetchedAt, bool FromCache);

/// <summary>
/// Reads the newest messages of a folder for previews and the setup assistant. Cached per account state, folder,
/// size and fetched data, so editing a rule does not hit the server again.
/// </summary>
internal sealed class MessageSampler(IMessageReader reader, IMemoryCache cache, TimeProvider time)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

    /// <summary>Throws <see cref="MailConnectionException"/> and <see cref="Mail.FolderNotFoundException"/>.</summary>
    public async Task<MessageSample> ReadAsync(
        MailAccount account,
        ImapConnectionSettings settings,
        string folder,
        int limit,
        FetchRequirements requirements,
        bool refresh,
        CancellationToken ct)
    {
        var key = (account.Id, account.UpdatedAt, folder, limit, requirements);
        if (!refresh && cache.TryGetValue(key, out MessageSample? cached) && cached is not null)
        {
            return cached with { FromCache = true };
        }

        var sample = new MessageSample(await reader.ReadLatestAsync(settings, folder, limit, requirements, ct), time.GetUtcNow(), FromCache: false);
        cache.Set(key, sample, CacheDuration);
        return sample;
    }

    /// <summary>Message ids in the Sent folder, when one of the rules needs them (IsReplyToMyMail).</summary>
    public async Task<IReadOnlySet<string>> SentIdsAsync(
        MailAccount account,
        ImapConnectionSettings settings,
        IEnumerable<RuleDefinition> rules,
        IReadOnlyDictionary<Guid, RuleDefinition> allRules,
        CancellationToken ct)
    {
        if (!FieldCatalog.UsedFields(rules, allRules).Contains(ConditionField.IsReplyToMyMail) || account.SpecialFolders.Sent is not { } sent)
        {
            return new HashSet<string>();
        }

        return await cache.GetOrCreateAsync((account.Id, account.UpdatedAt, "sent-ids"), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await reader.ReadMessageIdsAsync(settings, sent, 500, ct);
        }) ?? new HashSet<string>();
    }
}
