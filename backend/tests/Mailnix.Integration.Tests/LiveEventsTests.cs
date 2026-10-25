using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Core.Messages;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

public sealed class LiveEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task<(HttpClient Client, string Cookie)> SignInAsync(MailnixAppFactory app, string email)
    {
        var client = app.CreateApiClient();
        var response = await client.RegisterAsync(email);
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mailnix.auth=", StringComparison.Ordinal)).Split(';')[0];
        return (client, cookie);
    }

    internal static HubConnection Connect(MailnixAppFactory app, string cookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/live"), options =>
            {
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["Cookie"] = cookie;
            })
            .Build();

    [Fact]
    public async Task Users_receive_their_own_run_and_status_events_only()
    {
        var mail = new FakeMailServer();
        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:01")
            .WithServices(s => s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(mail)));
        var (owner, ownerCookie) = await SignInAsync(app, "owner@example.com");
        var (_, otherCookie) = await SignInAsync(app, "other@example.com");

        var ownerEvents = new List<string>();
        var otherEvents = new List<string>();
        await using var ownerHub = Connect(app, ownerCookie);
        await using var otherHub = Connect(app, otherCookie);
        ownerHub.On<JsonElement>("runUpdated", e => { lock (ownerEvents) { ownerEvents.Add($"run:{e.GetProperty("state").GetString()}"); } });
        ownerHub.On<JsonElement>("accountStatusChanged", e => { lock (ownerEvents) { ownerEvents.Add($"status:{e.GetProperty("status").GetString()}"); } });
        otherHub.On<JsonElement>("runUpdated", e => { lock (otherEvents) { otherEvents.Add("run"); } });
        await ownerHub.StartAsync(Ct);
        await otherHub.StartAsync(Ct);

        var accountId = await Seed.AccountAsync(app, "owner@example.com");
        mail.Append("INBOX", new MessageSnapshot { MessageId = "x@test", From = new EmailAddress("a@example.org"), Subject = "hi" });
        await owner.PostAsJsonAsync("/api/rules", new
        {
            accountId,
            name = "Flag all",
            conditions = new { kind = "condition", field = "Subject", @operator = "Exists" },
            actions = new object[] { new { type = "Flag" } },
        }, Ct);
        (await owner.PostAsJsonAsync($"/api/accounts/{accountId}/runs", new { limit = 10 }, Ct)).EnsureSuccessStatusCode();

        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            lock (ownerEvents)
            {
                if (ownerEvents.Contains("run:Completed"))
                {
                    break;
                }
            }

            await Task.Delay(50, Ct);
        }

        lock (ownerEvents)
        {
            ownerEvents.ShouldContain("run:Running");
            ownerEvents.ShouldContain("run:Completed");
        }

        lock (otherEvents)
        {
            otherEvents.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task The_hub_requires_sign_in()
    {
        await using var app = new MailnixAppFactory();
        await using var hub = Connect(app, "mailnix.auth=invalid");

        await Should.ThrowAsync<HttpRequestException>(() => hub.StartAsync(Ct));
    }
}
