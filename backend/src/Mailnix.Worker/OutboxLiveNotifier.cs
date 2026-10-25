using System.Text.Json;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Worker;

/// <summary>
/// Live events of a worker-only process: written to the database, from where every API process relays them to its
/// connected browsers (see LiveEventRelay). No message broker needed.
/// </summary>
public sealed class OutboxLiveNotifier(IServiceScopeFactory scopes, TimeProvider time) : ILiveNotifier
{
    /// <summary>The same JSON shape the SignalR hub sends.</summary>
    public static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public async Task NotifyAsync(Guid userId, string eventName, object payload)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        db.LiveEvents.Add(new LiveEventRecord
        {
            UserId = userId,
            Name = eventName,
            Payload = JsonSerializer.Serialize(payload, payload.GetType(), Json),
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }
}
