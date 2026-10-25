using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Runs;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Mailnix.Mail.Tests.RealServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Mailnix.Integration.Tests;

/// <summary>
/// The real worker against the real mailbox (if configured). Everything happens in a fresh subfolder of
/// INBOX.mailnix-test, which is the only folder the test account watches, and which is deleted afterwards.
/// </summary>
[Trait("Category", "RealServer")]
public sealed class RealServerWorkerTests
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static readonly ImapConnectionSettings? Settings = RealServerCredentials.Load();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_mail_is_processed_on_the_real_server_and_undone()
    {
        Assert.SkipWhen(Settings is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.500");
        var sessions = app.Services.GetRequiredService<IMailboxSessionFactory>();
        await using var helper = await sessions.OpenAsync(Settings!, Ct);
        var d = helper.Delimiter;
        var root = $"INBOX{d}mailnix-test{d}e2e-{Guid.NewGuid().ToString("N")[..8]}";
        var inbox = $"{root}{d}inbox";
        var done = $"{root}{d}done";
        await helper.CreateFolderAsync(inbox, Ct);

        try
        {
            var client = app.CreateApiClient();
            (await client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
            var created = await client.PostAsJsonAsync("/api/accounts", new
            {
                name = "Real (test folder only)",
                host = Settings!.Host,
                port = Settings.Port,
                security = Settings.Security.ToString(),
                username = Settings.Username,
                password = Settings.Password,
                watchedFolders = new[] { inbox },
            }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            var accountId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

            await EventuallyAsync(async () =>
            {
                await using var scope = app.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().FolderStates.AnyAsync(f => f.AccountId == accountId && f.Folder == inbox);
            });

            var rule = await client.PostAsJsonAsync("/api/rules", new
            {
                accountId,
                name = "e2e",
                conditions = new { kind = "condition", field = "Subject", @operator = "Contains", value = "mailnix-e2e" },
                actions = new object[] { new { type = "MarkRead" }, new { type = "Flag" }, new { type = "MoveTo", folder = done.Replace(d, '/') } },
            }, Ct);
            rule.StatusCode.ShouldBe(HttpStatusCode.Created);

            var message = new MimeMessage { Subject = "mailnix-e2e test", MessageId = $"e2e-{Guid.NewGuid():N}@mailnix.invalid", Body = new TextPart("plain") { Text = "test" } };
            message.From.Add(new MailboxAddress("mailnix test", "test@mailnix.invalid"));
            await helper.AppendAsync(inbox, message, Ct);

            // IDLE picks it up and the rule moves it into the (new) target folder.
            await EventuallyAsync(async () => await CountAsync(helper, done) == 1, timeoutSeconds: 60);
            {
                var moved = (await helper.FetchLatestAsync(done, 1, FetchRequirements.None, Ct)).Single();
                moved.IsSeen.ShouldBeTrue();
                moved.IsFlagged.ShouldBeTrue();
            }

            // The run is saved right after the move on the server, so it may not be listed yet.
            RunSummary run = null!;
            await EventuallyAsync(async () =>
                (run = (await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", Json, Ct))!.SingleOrDefault()!) is { State: Infrastructure.Data.RunState.Completed });

            // Counted for the stats: one new message, three actions (read, flag, move) by one rule.
            var overview = await client.GetFromJsonAsync<Api.Stats.OverviewResponse>("/api/stats/overview", Json, Ct);
            overview!.TodayKpis.ShouldSatisfyAllConditions(k => k.Processed.ShouldBe(1), k => k.Actions.ShouldBe(3), k => k.Moved.ShouldBe(1));
            var senders = await client.GetFromJsonAsync<Api.Stats.SendersResponse>("/api/stats/senders", Json, Ct);
            senders!.TopProcessed.Single().Domain.ShouldBe("mailnix.invalid");

            // Undo: back in the watched folder, flags restored, and not processed again.
            (await client.PostAsync($"/api/runs/{run.Id}/undo", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await EventuallyAsync(async () => await CountAsync(helper, inbox) == 1 && await CountAsync(helper, done) == 0, timeoutSeconds: 60);
            await Task.Delay(3000, Ct);
            (await CountAsync(helper, inbox)).ShouldBe(1);
            {
                var restored = (await helper.FetchLatestAsync(inbox, 1, FetchRequirements.None, Ct)).Single();
                restored.IsSeen.ShouldBeFalse();
                restored.IsFlagged.ShouldBeFalse();
            }
        }
        finally
        {
            await helper.DeleteFolderAsync(root, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Scheduled_rules_and_the_trash_purge_work_on_the_real_server()
    {
        Assert.SkipWhen(Settings is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.500")
            .WithSetting("Mailnix:Worker:ScheduleCheckInterval", "00:00:00.500");
        var sessions = app.Services.GetRequiredService<IMailboxSessionFactory>();
        await using var helper = await sessions.OpenAsync(Settings!, Ct);
        var d = helper.Delimiter;
        var root = $"INBOX{d}mailnix-test{d}e2e-{Guid.NewGuid().ToString("N")[..8]}";
        var inbox = $"{root}{d}inbox";
        var trash = $"{root}{d}trash";
        var done = $"{root}{d}done";

        MimeMessage Message(string subject)
        {
            var message = new MimeMessage { Subject = subject, MessageId = $"e2e-{Guid.NewGuid():N}@mailnix.invalid", Body = new TextPart("plain") { Text = "test" } };
            message.From.Add(new MailboxAddress("mailnix test", "test@mailnix.invalid"));
            return message;
        }

        try
        {
            await helper.AppendAsync(inbox, Message("mailnix-sched invoice"), Ct);
            await helper.AppendAsync(inbox, Message("something else"), Ct);
            await helper.AppendAsync(trash, Message("deleted earlier"), Ct);

            var client = app.CreateApiClient();
            (await client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
            var created = await client.PostAsJsonAsync("/api/accounts", new
            {
                name = "Real (test folder only)",
                host = Settings!.Host,
                port = Settings.Port,
                security = Settings.Security.ToString(),
                username = Settings.Username,
                password = Settings.Password,
                watchedFolders = new[] { inbox },
                specialFolders = new { trash },
                purgeTrashAfterDays = 1,
            }, Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
            var accountId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

            // The first purge pass only starts the clock for what is in Trash.
            await EventuallyAsync(async () =>
            {
                await using var scope = app.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().TrashObservations.AnyAsync(t => t.AccountId == accountId);
            });
            (await CountAsync(helper, trash)).ShouldBe(1);

            var rule = await client.PostAsJsonAsync("/api/rules", new
            {
                accountId,
                name = "scheduled e2e",
                trigger = "Scheduled",
                schedule = "*/5 * * * *",
                conditions = new { kind = "condition", field = "Subject", @operator = "Contains", value = "mailnix-sched" },
                actions = new object[] { new { type = "MoveTo", folder = done.Replace(d, '/') } },
            }, Ct);
            rule.StatusCode.ShouldBe(HttpStatusCode.Created);
            var ruleId = (await rule.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

            // Make the rule due now, and the Trash message a few days old (as far as mailnix knows).
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
                await db.Rules.Where(r => r.Id == ruleId).ExecuteUpdateAsync(u => u.SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-10)), Ct);
                await db.TrashObservations.Where(t => t.AccountId == accountId).ExecuteUpdateAsync(u => u.SetProperty(t => t.FirstSeenAt, DateTimeOffset.UtcNow.AddDays(-2)), Ct);
                await db.MailAccounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(u => u.SetProperty(a => a.LastPurgeAt, (DateTimeOffset?)null), Ct);
            }

            await EventuallyAsync(async () => await CountAsync(helper, done) == 1 && await CountAsync(helper, trash) == 0, timeoutSeconds: 60);
            (await CountAsync(helper, inbox)).ShouldBe(1);

            var runs = (await client.GetFromJsonAsync<List<RunSummary>>("/api/runs", Json, Ct))!;
            var scheduled = runs.Single(r => r.Trigger == Infrastructure.Data.RunTrigger.Scheduled);
            scheduled.SearchFilter.ShouldBe("SUBJECT \"mailnix-sched\"");
            scheduled.MessagesExamined.ShouldBe(1);
            runs.Where(r => r.Trigger == Infrastructure.Data.RunTrigger.Purge).Sum(r => r.ExecutedActions).ShouldBe(1);
        }
        finally
        {
            await helper.DeleteFolderAsync(root, CancellationToken.None);
        }
    }

    /// <summary>Message count via a fresh SELECT on the test's own connection (one connection, not one per check).</summary>
    private static async Task<int> CountAsync(IMailboxSession session, string folder)
    {
        try
        {
            return (await session.OpenAsync(folder, writable: false, Ct)).Count;
        }
        catch (Mail.FolderNotFoundException)
        {
            return 0;
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutSeconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(500);
        }
    }
}
