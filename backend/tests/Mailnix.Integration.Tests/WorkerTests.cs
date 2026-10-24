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

/// <summary>The real mailbox watcher, worker and run API against an in-memory mail server.</summary>
public sealed class WorkerTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly FakeMailServer _mail = new();
    private readonly MailnixAppFactory _app;
    private HttpClient _client = null!;
    private Guid _accountId;

    public WorkerTests()
    {
        _app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:01")
            .WithSetting("Mailnix:Worker:PollInterval", "00:00:00.500")
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

    private static int _messageCounter;

    private static MessageSnapshot Mail(string subject, bool newsletter = false, string from = "news@shop.example") => new()
    {
        MessageId = $"m{Interlocked.Increment(ref _messageCounter)}@test",
        From = new EmailAddress(from),
        To = [new EmailAddress("owner@example.com")],
        Subject = subject,
        ReceivedAt = DateTimeOffset.UtcNow,
        Size = 1000,
        Headers = newsletter ? [new("List-Unsubscribe", "<mailto:u@shop.example>")] : [],
    };

    private async Task StartWatchingAsync(Action<MailAccount>? configure = null)
    {
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
            var account = await db.MailAccounts.SingleAsync(a => a.Id == _accountId);
            account.FolderDelimiter = ".";
            account.SpecialFolders = new MailAccountSpecialFolders { Trash = "INBOX.Trash", Archive = "INBOX.Archive" };
            configure?.Invoke(account);
            await db.SaveChangesAsync();
        }

        // The watcher starts with a snapshot of the folders: only mail arriving afterwards counts as new.
        await EventuallyAsync(async () =>
        {
            await using var scope = _app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
            var watched = (await db.MailAccounts.SingleAsync(a => a.Id == _accountId)).WatchedFolders;
            return await db.FolderStates.CountAsync(f => f.AccountId == _accountId) == watched.Count;
        });
    }

    private async Task<Guid> CreateRuleAsync(string name, object conditions, object[] actions, bool dryRun = false, string[]? sourceFolders = null)
    {
        var response = await _client.PostAsJsonAsync("/api/rules", new { accountId = _accountId, name, conditions, actions, dryRun, sourceFolders }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static readonly object Newsletter = new { kind = "condition", field = "IsNewsletter", @operator = "IsTrue" };

    private async Task<List<RunSummary>> RunsAsync() => (await _client.GetFromJsonAsync<List<RunSummary>>("/api/runs", Json, Ct))!;

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
    public async Task New_mail_is_sorted_existing_mail_is_left_alone_and_undo_puts_it_back()
    {
        _mail.Append("INBOX", Mail("old newsletter", newsletter: true));
        await StartWatchingAsync();
        await CreateRuleAsync("Newsletters", Newsletter, [new { type = "MarkRead" }, new { type = "MoveTo", folder = "INBOX/Newsletters/{year}" }]);

        _mail.Append("INBOX", Mail("Autumn sale", newsletter: true));
        _mail.Append("INBOX", Mail("Hello", from: "friend@example.org"));

        var target = $"INBOX.Newsletters.{DateTimeOffset.UtcNow.Year}";
        await EventuallyAsync(() => Task.FromResult(_mail.Messages(target).Count == 1));
        _mail.Messages(target).Single().ShouldSatisfyAllConditions(m => m.Subject.ShouldBe("Autumn sale"), m => m.IsSeen.ShouldBeTrue());
        _mail.Messages("INBOX").Select(m => m.Subject).ShouldBe(["old newsletter", "Hello"], ignoreOrder: true);

        RunSummary run = null!;
        await EventuallyAsync(async () => (run = (await RunsAsync()).Single()).State == Infrastructure.Data.RunState.Completed);
        run.Trigger.ShouldBe(RunTrigger.NewMail);
        run.ExecutedActions.ShouldBe(2);
        run.CanUndo.ShouldBeTrue();
        var detail = await _client.GetFromJsonAsync<RunDetail>($"/api/runs/{run.Id}", Json, Ct);
        detail!.Actions.Select(a => a.Action).ShouldBe([ActionType.MarkRead, ActionType.MoveTo], ignoreOrder: true);
        detail.Actions.ShouldAllBe(a => a.Success && a.Subject == "Autumn sale");

        // Undo: back to the inbox, unread again.
        (await _client.PostAsync($"/api/runs/{run.Id}/undo", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(() => Task.FromResult(_mail.Messages("INBOX").Any(m => m.Subject == "Autumn sale")));
        _mail.Messages("INBOX").Single(m => m.Subject == "Autumn sale").IsSeen.ShouldBeFalse();
        _mail.Messages(target).ShouldBeEmpty();
        await EventuallyAsync(async () => (await RunsAsync()).Single(r => r.Id == run.Id).UndoneByRunId != null);
        (await RunsAsync()).Single(r => r.Id == run.Id).CanUndo.ShouldBeFalse();

        // The message moved back by undo is not processed again (it was moved there by mailnix).
        await Task.Delay(1500, Ct);
        _mail.Messages(target).ShouldBeEmpty();
    }

    [Fact]
    public async Task Runs_above_the_threshold_wait_for_confirmation()
    {
        await StartWatchingAsync(a => a.BlastRadiusLimit = 2);
        await CreateRuleAsync("Archive newsletters", Newsletter, [new { type = "Archive" }]);

        for (var i = 0; i < 3; i++)
        {
            _mail.Append("INBOX", Mail($"News {i}", newsletter: true));
        }

        RunSummary run = null!;
        await EventuallyAsync(async () => (run = (await RunsAsync()).FirstOrDefault()!) is { State: Infrastructure.Data.RunState.AwaitingConfirmation });
        _mail.Messages("INBOX").Count.ShouldBe(3);
        var detail = await _client.GetFromJsonAsync<RunDetail>($"/api/runs/{run.Id}", Json, Ct);
        detail!.Pending.Count.ShouldBe(3);
        detail.Pending[0].Actions.Single().ShouldBe(new PendingActionItem("Archive newsletters", ActionType.Archive, "INBOX.Archive", null, false));

        // One message disappears before confirming: the rest is processed, the missing one is reported.
        await using (var session = await _mail.OpenAsync(null!, Ct))
        {
            await session.MoveAsync("INBOX", _mail.Messages("INBOX")[0].Uid, "INBOX.Elsewhere", Ct);
        }

        (await _client.PostAsync($"/api/runs/{run.Id}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyAsync(async () => (await RunsAsync()).Single(r => r.Id == run.Id).State == Infrastructure.Data.RunState.CompletedWithErrors);
        _mail.Messages("INBOX.Archive").Count.ShouldBe(2);
        var finished = (await RunsAsync()).Single(r => r.Id == run.Id);
        finished.ExecutedActions.ShouldBe(2);
        finished.FailedActions.ShouldBe(1);
        (await _client.PostAsync($"/api/runs/{run.Id}/confirm", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancelled_runs_change_nothing()
    {
        await StartWatchingAsync(a => a.BlastRadiusLimit = 1);
        await CreateRuleAsync("Trash newsletters", Newsletter, [new { type = "Delete" }]);
        _mail.Append("INBOX", Mail("a", newsletter: true));
        _mail.Append("INBOX", Mail("b", newsletter: true));

        RunSummary run = null!;
        await EventuallyAsync(async () => (run = (await RunsAsync()).FirstOrDefault()!) is { State: Infrastructure.Data.RunState.AwaitingConfirmation });
        (await _client.PostAsync($"/api/runs/{run.Id}/cancel", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Task.Delay(1500, Ct);
        _mail.Messages("INBOX").Count.ShouldBe(2);
        (await RunsAsync()).Single().State.ShouldBe(Infrastructure.Data.RunState.Cancelled);
    }

    [Fact]
    public async Task Manual_runs_process_existing_mail_and_rule_actions_can_be_undone()
    {
        _mail.Append("INBOX", Mail("existing 1", newsletter: true));
        _mail.Append("INBOX", Mail("existing 2", newsletter: true));
        _mail.Append("INBOX", Mail("personal", from: "friend@example.org"));
        await StartWatchingAsync();
        var rule = await CreateRuleAsync("Flag newsletters", Newsletter, [new { type = "Flag" }, new { type = "AddKeyword", keyword = "news" }]);

        var started = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/runs", new { limit = 10 }, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(async () => (await RunsAsync()).Single().State == Infrastructure.Data.RunState.Completed);
        _mail.Messages("INBOX").Where(m => m.IsFlagged).Select(m => m.Subject).ShouldBe(["existing 1", "existing 2"], ignoreOrder: true);
        (await RunsAsync()).Single().MessagesExamined.ShouldBe(3);

        // Undo only the last action of the rule (newest message first).
        (await _client.PostAsJsonAsync($"/api/rules/{rule}/undo", new { count = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(async () => (await RunsAsync()).Count(r => r.State == Infrastructure.Data.RunState.Completed) == 2);
        var flagged = _mail.Messages("INBOX").Where(m => m.IsFlagged).ToList();
        flagged.Count.ShouldBe(1);
        _mail.Messages("INBOX").Count(m => m.Keywords.Contains("news")).ShouldBe(1);
    }

    [Fact]
    public async Task Dry_run_rules_only_log()
    {
        await StartWatchingAsync();
        await CreateRuleAsync("Trial", Newsletter, [new { type = "Delete" }], dryRun: true);

        _mail.Append("INBOX", Mail("news", newsletter: true));

        RunSummary run = null!;
        await EventuallyAsync(async () => (run = (await RunsAsync()).FirstOrDefault()!) is { State: Infrastructure.Data.RunState.Completed });
        _mail.Messages("INBOX").Single().Subject.ShouldBe("news");
        var detail = await _client.GetFromJsonAsync<RunDetail>($"/api/runs/{run.Id}", Json, Ct);
        detail!.Actions.Single().ShouldSatisfyAllConditions(a => a.DryRun.ShouldBeTrue(), a => a.TargetFolder.ShouldBe("INBOX.Trash"));
        run.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task Rules_cannot_ping_pong_messages_between_watched_folders()
    {
        _mail.CreateFolder("INBOX.Sorted");
        await StartWatchingAsync(a => a.WatchedFolders = ["INBOX", "INBOX.Sorted"]);
        await CreateRuleAsync("To sorted", Newsletter, [new { type = "MoveTo", folder = "INBOX/Sorted" }], sourceFolders: ["INBOX"]);
        await CreateRuleAsync("Back to inbox", Newsletter, [new { type = "MoveTo", folder = "INBOX" }], sourceFolders: ["INBOX.Sorted"]);

        _mail.Append("INBOX", Mail("bounce me", newsletter: true));

        await EventuallyAsync(() => Task.FromResult(_mail.Messages("INBOX.Sorted").Count == 1));
        await Task.Delay(2000, Ct); // several passes
        _mail.Messages("INBOX.Sorted").Count.ShouldBe(1);
        (await RunsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Connection_problems_are_reported_and_recovered_from()
    {
        _mail.ConnectError = new MailConnectionException(ConnectionErrorCode.Timeout, "The mail server did not respond in time.");
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");

        await EventuallyAsync(async () =>
            (await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{_accountId}", Ct)).GetProperty("lastErrorCode").GetString() == "Timeout");

        _mail.ConnectError = null;
        await EventuallyAsync(async () =>
            (await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{_accountId}", Ct)).GetProperty("status").GetString() == "Ok");
    }

    [Fact]
    public async Task Runs_of_paused_accounts_are_refused()
    {
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
            (await db.MailAccounts.SingleAsync(Ct)).Enabled = false;
            await db.SaveChangesAsync(Ct);
        }

        var response = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/runs", new { limit = 10 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("AccountPaused");
    }
}
