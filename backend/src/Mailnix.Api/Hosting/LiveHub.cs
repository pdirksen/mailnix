using Mailnix.Worker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Mailnix.Api.Hosting;

/// <summary>Server → browser events (account status, run progress). Each user only receives their own.</summary>
[Authorize]
public sealed class LiveHub : Hub
{
    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(Context.User!.GetUserId()));
        await base.OnConnectedAsync();
    }
}

internal sealed class HubLiveNotifier(IHubContext<LiveHub> hub) : ILiveNotifier
{
    public Task NotifyAsync(Guid userId, string eventName, object payload) =>
        hub.Clients.Group(LiveHub.UserGroup(userId)).SendAsync(eventName, payload);
}
