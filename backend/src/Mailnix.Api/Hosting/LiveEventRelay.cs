using System.Text.Json;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Hosting;

/// <summary>
/// API processes of a split deployment: sends live events that worker processes wrote to the database to the
/// browsers connected here. Each API process relays every event to its own connections; old events are deleted.
/// </summary>
internal sealed partial class LiveEventRelay(
    IServiceScopeFactory scopes,
    IHubContext<LiveHub> hub,
    TimeProvider time,
    ILogger<LiveEventRelay> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long last;
        await using (var scope = scopes.CreateAsyncScope())
        {
            // Only what happens from now on: earlier events were for earlier connections.
            last = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().LiveEvents.MaxAsync(e => (long?)e.Id, stoppingToken) ?? 0;
        }

        var lastCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, time, stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
                var events = await db.LiveEvents.AsNoTracking().Where(e => e.Id > last).OrderBy(e => e.Id).Take(500).ToListAsync(stoppingToken);
                foreach (var live in events)
                {
                    using var payload = JsonDocument.Parse(live.Payload);
                    await hub.Clients.Group(LiveHub.UserGroup(live.UserId)).SendAsync(live.Name, payload.RootElement.Clone(), stoppingToken);
                    last = live.Id;
                }

                var now = time.GetUtcNow();
                if (now - lastCleanup > TimeSpan.FromMinutes(1))
                {
                    lastCleanup = now;
                    await db.LiveEvents.Where(e => e.CreatedAt < now - KeepFor).ExecuteDeleteAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or JsonException)
            {
                LogRelayFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not relay live events")]
    private partial void LogRelayFailed(Exception exception);
}
