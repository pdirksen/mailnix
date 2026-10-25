using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Core.Messages;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

/// <summary>SaaS deployment: API and worker in separate processes that only share the database.</summary>
public sealed class SplitDeploymentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_run_queued_through_the_api_is_executed_by_a_worker_process_and_reported_live()
    {
        var mail = new FakeMailServer();
        await using var api = new MailnixAppFactory().WithSetting("Mailnix:Mode", "Api");
        var (owner, cookie) = await LiveEventsTests.SignInAsync(api, "owner@example.com");
        var accountId = await Seed.AccountAsync(api, "owner@example.com");
        mail.Append("INBOX", new MessageSnapshot { MessageId = "x@test", From = new EmailAddress("a@example.org"), Subject = "hi" });
        (await owner.PostAsJsonAsync("/api/rules", new
        {
            accountId,
            name = "Flag all",
            conditions = new { kind = "condition", field = "Subject", @operator = "Exists" },
            actions = new object[] { new { type = "Flag" } },
        }, Ct)).EnsureSuccessStatusCode();

        var events = new List<string>();
        await using var hub = LiveEventsTests.Connect(api, cookie);
        hub.On<JsonElement>("runUpdated", e => { lock (events) { events.Add(e.GetProperty("state").GetString()!); } });
        await hub.StartAsync(Ct);

        await using var worker = new MailnixAppFactory()
            .SharingDatabaseWith(api)
            .WithSetting("Mailnix:Mode", "Worker")
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:QueuePollInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:01")
            .WithServices(s => s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(mail)));
        _ = worker.Services; // starts the worker process

        // The API process has no watchers; only the worker can pick this up.
        (await owner.PostAsJsonAsync($"/api/accounts/{accountId}/runs", new { limit = 10 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until && !Has(events, "Completed"))
        {
            await Task.Delay(50, Ct);
        }

        Has(events, "Completed").ShouldBeTrue("the worker's events reach the browser through the API process");
        mail.Messages("INBOX").Single().IsFlagged.ShouldBeTrue();

        await using var scope = api.Services.CreateAsyncScope();
        var lease = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailboxLeases.SingleAsync(l => l.AccountId == accountId, Ct);
        lease.WorkerId.ShouldNotBeNull();
        lease.WorkerId.ShouldContain(Environment.MachineName);
    }

    private static bool Has(List<string> events, string state)
    {
        lock (events)
        {
            return events.Contains(state);
        }
    }
}
