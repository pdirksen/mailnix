using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Runs;
using Mailnix.Api.Stats;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Mailnix.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

/// <summary>Daily counters written by the worker, the stats endpoints, the action list and undoing a selection.</summary>
public sealed class StatsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly FakeMailServer _mail = new();
    private readonly MailnixAppFactory _app;
    private HttpClient _client = null!;
    private Guid _accountId;

    public StatsTests()
    {
        _app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:01")
            .WithSetting("Mailnix:Worker:MinBackoff", "00:00:00.200")
            .WithServices(s => s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(_mail)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static int _counter;

    private static MessageSnapshot Mail(string subject, string from, bool newsletter = false, string? auth = null)
    {
        List<KeyValuePair<string, string>> headers = [new("From", from), new("Subject", subject)];
        if (newsletter)
        {
            headers.Add(new("List-Unsubscribe", "<mailto:u@shop.example>"));
        }

        if (auth is not null)
        {
            headers.Insert(0, new("Authentication-Results", auth));
        }

        return new MessageSnapshot
        {
            MessageId = $"st{Interlocked.Increment(ref _counter)}@test",
            From = new EmailAddress(from),
            To = [new EmailAddress("owner@example.com")],
            Subject = subject,
            ReceivedAt = DateTimeOffset.UtcNow,
            Size = 1000,
            Headers = headers,
        };
    }

    private async Task StartWatchingAsync()
    {
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
            var account = await db.MailAccounts.SingleAsync(a => a.Id == _accountId);
            account.FolderDelimiter = ".";
            account.SpecialFolders = new MailAccountSpecialFolders { Trash = "INBOX.Trash" };
            await db.SaveChangesAsync();
        }

        await EventuallyAsync(async () =>
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().FolderStates.AnyAsync(f => f.AccountId == _accountId);
        });
    }

    private async Task<Guid> CreateRuleAsync(string name, object conditions, object[] actions)
    {
        var response = await _client.PostAsJsonAsync("/api/rules", new { accountId = _accountId, name, conditions, actions }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private async Task<T> GetAsync<T>(string url) => (await _client.GetFromJsonAsync<T>(url, Json, Ct))!;

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50);
        }
    }

    private static readonly object Newsletter = new { kind = "condition", field = "IsNewsletter", @operator = "IsTrue" };

    /// <summary>Two newsletters (one failing DMARC) and a personal mail arrive; the newsletters are moved.</summary>
    private async Task<Guid> ProcessSomeMailAsync()
    {
        _mail.Append("INBOX", Mail("old", "old@shop.example", newsletter: true));
        await StartWatchingAsync();
        var ruleId = await CreateRuleAsync("Newsletters", Newsletter, [new { type = "MoveTo", folder = "INBOX/News" }]);

        _mail.Append("INBOX", Mail("Deals", "news@shop.example", newsletter: true, auth: "mx.example.org; spf=pass smtp.mailfrom=shop.example; dkim=pass header.d=shop.example; dmarc=pass"));
        _mail.Append("INBOX", Mail("Fake deals", "news@fake.example", newsletter: true, auth: "mx.example.org; spf=fail smtp.mailfrom=fake.example; dkim=none; dmarc=fail header.from=fake.example"));
        _mail.Append("INBOX", Mail("Hello", "friend@example.org"));
        await EventuallyAsync(() => Task.FromResult(_mail.Messages("INBOX.News").Count == 2));
        await EventuallyAsync(async () => (await GetAsync<OverviewResponse>("/api/stats/overview")).TodayKpis.Processed == 3);
        return ruleId;
    }

    [Fact]
    public async Task New_mail_and_actions_are_counted_for_the_dashboard()
    {
        var ruleId = await ProcessSomeMailAsync();

        var overview = await GetAsync<OverviewResponse>("/api/stats/overview");
        overview.TodayKpis.ShouldBe(new KpiSet(Processed: 3, Actions: 2, Moved: 2, Trashed: 0, Flagged: 0, Failed: 0));
        overview.YesterdayKpis.Processed.ShouldBe(0);
        overview.Accounts.Single().ShouldSatisfyAllConditions(a => a.ProcessedToday.ShouldBe(3), a => a.ActionsToday.ShouldBe(2));
        overview.TopRules.Single().ShouldBe(new RuleCount(ruleId, "Newsletters", _accountId, 2));
        overview.Activity.Series.Single().ShouldSatisfyAllConditions(s => s.Key.ShouldBe("MoveTo"), s => s.Values[^1].ShouldBe(2));
        overview.Activity.Buckets.Count.ShouldBe(30);
        overview.Hints.ShouldBeEmpty();

        // The existing message and messages mailnix moved itself are not counted as new mail.
        var processed = await GetAsync<TimeSeries>("/api/stats/timeseries?metric=processed&groupBy=none");
        processed.Series.Single().Values.Sum().ShouldBe(3);

        var rules = await GetAsync<RuleStatsResponse>("/api/stats/rules");
        rules.Rules.Single().ShouldSatisfyAllConditions(
            r => r.Matched.ShouldBe(2),
            r => r.Actions.ShouldBe(2),
            r => r.LastMatch.ShouldBe(overview.Today));
    }

    [Fact]
    public async Task Senders_security_and_folders_are_counted()
    {
        await ProcessSomeMailAsync();

        var senders = await GetAsync<SendersResponse>("/api/stats/senders");
        senders.Totals.ShouldBe(new SenderRow(string.Empty, Processed: 3, Actioned: 2, Bulk: 2, AuthFailed: 1));
        senders.TopActioned.Select(s => s.Domain).ShouldBe(["fake.example", "shop.example"], ignoreOrder: true);

        var security = await GetAsync<SecurityResponse>("/api/stats/security");
        security.Totals.ShouldBe(new SecurityTotals(Processed: 3, SpfFailed: 1, DkimFailed: 0, DmarcFailed: 1, Spoofing: 0, HighSpam: 0));
        security.TopFailingDomains.Single().Domain.ShouldBe("fake.example");

        var folders = await GetAsync<FoldersResponse>("/api/stats/folders");
        folders.Date.ShouldNotBeNull();
        folders.Folders.ShouldContain(f => f.Folder == "INBOX");
        folders.Growth.Series.Select(s => s.Key).ShouldBe(["messages", "unread"], ignoreOrder: true);
    }

    [Fact]
    public async Task Actions_are_listed_and_a_selection_can_be_undone()
    {
        var ruleId = await ProcessSomeMailAsync();

        var page = await GetAsync<ActionPage>($"/api/activity/actions?ruleId={ruleId}&limit=1");
        page.Items.Single().ShouldSatisfyAllConditions(a => a.Action.ShouldBe(ActionType.MoveTo), a => a.CanUndo.ShouldBeTrue());
        page.NextBefore.ShouldNotBeNull();
        var older = await GetAsync<ActionPage>($"/api/activity/actions?ruleId={ruleId}&before={page.NextBefore}");
        older.Items.Count.ShouldBe(1);
        older.NextBefore.ShouldBeNull();
        (await GetAsync<ActionPage>("/api/activity/actions?action=Delete")).Items.ShouldBeEmpty();

        var undo = await _client.PostAsJsonAsync("/api/activity/actions/undo", new { actionIds = new[] { page.Items[0].Id } }, Ct);
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(() => Task.FromResult(_mail.Messages("INBOX.News").Count == 1));

        await EventuallyAsync(async () => (await GetAsync<RuleStatsResponse>("/api/stats/rules")).Rules.Single().Undone == 1);
        var after = await GetAsync<ActionPage>($"/api/activity/actions?ruleId={ruleId}");
        after.Items.Count.ShouldBe(2, "the undo's own bookkeeping is not listed");
        after.Items.Single(a => a.Id == page.Items[0].Id).ShouldSatisfyAllConditions(a => a.UndoneAt.ShouldNotBeNull(), a => a.CanUndo.ShouldBeFalse());

        var again = await _client.PostAsJsonAsync("/api/activity/actions/undo", new { actionIds = new[] { page.Items[0].Id } }, Ct);
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sections_export_as_spreadsheet_safe_csv()
    {
        await ProcessSomeMailAsync();
        await CreateRuleAsync("=HYPERLINK(\"http://evil\")", Newsletter, [new { type = "Flag" }]);

        var response = await _client.GetAsync("/api/stats/export?section=rules", Ct);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var csv = await response.Content.ReadAsStringAsync(Ct);
        csv.ShouldStartWith("rule,account,enabled");
        csv.ShouldContain("\"'=HYPERLINK(\"\"http://evil\"\")\"");
        csv.ShouldContain("Newsletters,Work,True,False,2,2,0,0,0,");

        (await _client.GetAsync("/api/stats/export?section=volume", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync("/api/stats/export?section=nope", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Old_runs_are_removed_but_their_stats_stay()
    {
        await ProcessSomeMailAsync();
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        await db.Runs.ExecuteUpdateAsync(u => u.SetProperty(r => r.CreatedAt, DateTimeOffset.UtcNow.AddDays(-100)), Ct);

        (await RuleScheduler.DeleteOldRunsAsync(db, DateTimeOffset.UtcNow.AddDays(-90), Ct)).ShouldBe(1);

        (await db.ActionLog.CountAsync(Ct)).ShouldBe(0);
        (await GetAsync<OverviewResponse>("/api/stats/overview")).TodayKpis.Moved.ShouldBe(2);
    }

    [Fact]
    public async Task Users_see_only_their_own_stats()
    {
        await ProcessSomeMailAsync();
        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();

        var overview = await other.GetFromJsonAsync<OverviewResponse>("/api/stats/overview", Json, Ct);
        overview!.TodayKpis.Processed.ShouldBe(0);
        overview.Accounts.ShouldBeEmpty();
        overview.Hints.Single().Kind.ShouldBe(HintKind.NoRules);
        (await other.GetFromJsonAsync<ActionPage>("/api/activity/actions", Json, Ct))!.Items.ShouldBeEmpty();
    }
}
