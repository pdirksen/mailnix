using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Runs;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

/// <summary>Scheduled rules, server-side search and the Trash purge, with the real worker against an in-memory mail server.</summary>
public sealed class SchedulingTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly FakeMailServer _mail = new();
    private readonly MailnixAppFactory _app;
    private HttpClient _client = null!;
    private Guid _accountId;

    public SchedulingTests()
    {
        _app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:01")
            .WithSetting("Mailnix:Worker:MinBackoff", "00:00:00.200")
            .WithSetting("Mailnix:Worker:ScheduleCheckInterval", "00:00:00.200")
            .WithServices(s => s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(_mail)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static int _messageCounter;

    private static MessageSnapshot Mail(string subject, string from = "billing@shop.example") => new()
    {
        MessageId = $"s{Interlocked.Increment(ref _messageCounter)}@test",
        From = new EmailAddress(from),
        To = [new EmailAddress("owner@example.com")],
        Subject = subject,
        ReceivedAt = DateTimeOffset.UtcNow.AddDays(-3),
        Size = 1000,
        Headers = [],
    };

    private async Task StartWatchingAsync(Action<MailAccount>? configure = null)
    {
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        await UpdateAsync<MailAccount>(a => a.Id == _accountId, account =>
        {
            account.FolderDelimiter = ".";
            account.SpecialFolders = new MailAccountSpecialFolders { Trash = "INBOX.Trash" };
            configure?.Invoke(account);
        });
        await EventuallyAsync(() => WithDbAsync(db => db.FolderStates.AnyAsync(f => f.AccountId == _accountId)));
    }

    private async Task<Guid> CreateRuleAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/rules", body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static readonly object Invoices = new { kind = "condition", field = "Subject", @operator = "Contains", value = "invoice" };

    private async Task<List<RunSummary>> RunsAsync() => (await _client.GetFromJsonAsync<List<RunSummary>>("/api/runs", Json, Ct))!;

    private async Task<T> WithDbAsync<T>(Func<MailnixDbContext, Task<T>> action)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<MailnixDbContext>());
    }

    private async Task UpdateAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> where, Action<T> change)
        where T : class
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        foreach (var entity in await db.Set<T>().Where(where).ToListAsync())
        {
            change(entity);
        }

        await db.SaveChangesAsync();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutSeconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_scheduled_rule_runs_when_due_over_existing_mail_preselected_by_the_server()
    {
        _mail.Append("INBOX", Mail("Your invoice 42"));
        _mail.Append("INBOX", Mail("Hello", from: "friend@example.org"));
        _mail.Append("INBOX", Mail("Weekly deals"));
        await StartWatchingAsync();
        var ruleId = await CreateRuleAsync(new
        {
            accountId = _accountId,
            name = "Invoices",
            trigger = "Scheduled",
            schedule = "*/5 * * * *",
            conditions = Invoices,
            actions = new object[] { new { type = "MoveTo", folder = "INBOX/Invoices" } },
        });

        // Not due right after saving: the schedule starts from the last change.
        await Task.Delay(600, Ct);
        (await RunsAsync()).ShouldBeEmpty();
        var rule = (await _client.GetFromJsonAsync<JsonElement>($"/api/rules/{ruleId}", Ct)).GetProperty("nextRunAt").GetDateTimeOffset();
        rule.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        rule.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(5));

        // As if it was saved ten minutes ago: due now.
        await UpdateAsync<MailRule>(r => r.Id == ruleId, r => r.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        await EventuallyAsync(() => Task.FromResult(_mail.Messages("INBOX.Invoices").Count == 1));

        _mail.Messages("INBOX.Invoices").Single().Subject.ShouldBe("Your invoice 42");
        _mail.Messages("INBOX").Count.ShouldBe(2);
        _mail.Searches.ShouldContain(f => f != null && f.Describe() == "SUBJECT \"invoice\"");

        await EventuallyAsync(async () => (await RunsAsync()).Any(r => r.State == RunState.Completed));
        var run = (await RunsAsync()).Single();
        run.Trigger.ShouldBe(RunTrigger.Scheduled);
        run.SearchFilter.ShouldBe("SUBJECT \"invoice\"");
        run.MessagesExamined.ShouldBe(1);
        run.ExecutedActions.ShouldBe(1);

        // Not again until the next occurrence.
        await Task.Delay(800, Ct);
        (await RunsAsync()).Count.ShouldBe(1);
        (await WithDbAsync(db => db.Rules.SingleAsync(r => r.Id == ruleId))).LastScheduledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Switched_off_scheduled_rules_do_not_run()
    {
        _mail.Append("INBOX", Mail("Your invoice 42"));
        await StartWatchingAsync();
        var ruleId = await CreateRuleAsync(new
        {
            accountId = _accountId,
            name = "Invoices",
            enabled = false,
            trigger = "Scheduled",
            schedule = "* * * * *",
            conditions = Invoices,
            actions = new object[] { new { type = "Flag" } },
        });
        await UpdateAsync<MailRule>(r => r.Id == ruleId, r => r.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10));

        await Task.Delay(1000, Ct);
        (await RunsAsync()).ShouldBeEmpty();
        _mail.Messages("INBOX").Single().IsFlagged.ShouldBeFalse();
    }

    [Fact]
    public async Task Run_now_reports_the_search_and_checks_only_candidates()
    {
        foreach (var subject in new[] { "invoice 1", "Hello", "invoice 2", "Deals" })
        {
            _mail.Append("INBOX", Mail(subject));
        }

        await StartWatchingAsync();
        await CreateRuleAsync(new { accountId = _accountId, name = "Invoices", conditions = Invoices, actions = new object[] { new { type = "Flag" } } });

        (await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/runs", new { limit = 10 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(async () => (await RunsAsync()).Any(r => r.State == RunState.Completed));

        var run = (await RunsAsync()).Single();
        run.SearchFilter.ShouldBe("SUBJECT \"invoice\"");
        run.MessagesExamined.ShouldBe(2);
        _mail.Messages("INBOX").Where(m => m.IsFlagged).Select(m => m.Subject).ShouldBe(["invoice 1", "invoice 2"], ignoreOrder: true);
    }

    [Fact]
    public async Task Rules_the_server_cannot_check_examine_every_message()
    {
        foreach (var subject in new[] { "invoice 1", "Hello" })
        {
            _mail.Append("INBOX", Mail(subject));
        }

        await StartWatchingAsync();
        await CreateRuleAsync(new
        {
            accountId = _accountId,
            name = "Regex",
            conditions = new { kind = "condition", field = "Subject", @operator = "Regex", value = "^invoice \\d$" },
            actions = new object[] { new { type = "Flag" } },
        });

        (await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/runs", new { limit = 10 }, Ct)).EnsureSuccessStatusCode();
        await EventuallyAsync(async () => (await RunsAsync()).Any(r => r.State == RunState.Completed));

        var run = (await RunsAsync()).Single();
        run.SearchFilter.ShouldBeNull();
        run.MessagesExamined.ShouldBe(2);
        run.ExecutedActions.ShouldBe(1);
    }

    [Fact]
    public async Task Trash_is_purged_only_after_messages_were_seen_there_long_enough()
    {
        _mail.Append("INBOX.Trash", Mail("deleted long ago"));
        _mail.Append("INBOX.Trash", Mail("deleted recently"));
        await StartWatchingAsync(a => a.PurgeTrashAfterDays = 7);

        // First pass: mailnix starts counting; nothing is old enough yet, whatever the arrival dates say.
        await EventuallyAsync(async () => (await RunsAsync()).Any(r => r.Trigger == RunTrigger.Purge && r.State == RunState.Completed));
        (await RunsAsync()).Single().ExecutedActions.ShouldBe(0);
        _mail.Messages("INBOX.Trash").Count.ShouldBe(2);
        (await WithDbAsync(db => db.TrashObservations.CountAsync(t => t.AccountId == _accountId))).ShouldBe(2);

        // A week later for one of them.
        var oldUid = _mail.Messages("INBOX.Trash").Single(m => m.Subject == "deleted long ago").Uid;
        await UpdateAsync<TrashObservation>(t => t.AccountId == _accountId && t.Uid == oldUid, t => t.FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-8));
        await UpdateAsync<MailAccount>(a => a.Id == _accountId, a => a.LastPurgeAt = null);

        await EventuallyAsync(() => Task.FromResult(_mail.Messages("INBOX.Trash").Count == 1));
        _mail.Messages("INBOX.Trash").Single().Subject.ShouldBe("deleted recently");

        await EventuallyAsync(async () => (await RunsAsync()).Count(r => r.State == RunState.Completed) == 2);
        var purge = (await RunsAsync()).First();
        purge.ExecutedActions.ShouldBe(1);
        purge.CanUndo.ShouldBeFalse();
        var detail = await _client.GetFromJsonAsync<RunDetail>($"/api/runs/{purge.Id}", Json, Ct);
        detail!.Actions.Single().ShouldSatisfyAllConditions(a => a.Action.ShouldBe(ActionType.Purge), a => a.Subject.ShouldBe("deleted long ago"));
    }

    [Fact]
    public async Task Servers_without_uidplus_are_not_purged()
    {
        _mail.UidPlus = false;
        _mail.Append("INBOX.Trash", Mail("deleted"));
        await StartWatchingAsync(a => a.PurgeTrashAfterDays = 1);

        await EventuallyAsync(async () => (await RunsAsync()).Any(r => r.State == RunState.Failed));
        (await RunsAsync()).Single().Error!.ShouldContain("UIDPLUS");
        _mail.Messages("INBOX.Trash").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Purging_a_watched_folder_is_refused()
    {
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        var response = await _client.PutAsJsonAsync($"/api/accounts/{_accountId}", new
        {
            name = "Work",
            host = "imap.example.com",
            port = 993,
            security = "SslOnConnect",
            username = "owner@example.com",
            watchedFolders = new[] { "INBOX" },
            specialFolders = new { trash = "INBOX" },
            enabled = true,
            blastRadiusLimit = 200,
            purgeTrashAfterDays = 30,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("purgeTrashAfterDays");
    }
}
