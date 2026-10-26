using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Assistant;
using Mailnix.Api.Rules;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

public sealed class AssistantEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly FakeMessageReader _reader = new();
    private readonly FakeMailServer _mail = new();
    private readonly MailnixAppFactory _app;
    private HttpClient _client = null!;
    private Guid _accountId;

    public AssistantEndpointsTests()
    {
        _app = new MailnixAppFactory().WithServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IMessageReader>(_reader));
            s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(_mail));
        });
        _reader.Folders["INBOX"].AddRange(
        [
            FakeMessageReader.Message(1, "news@shop.example", "Autumn sale", newsletter: true),
            FakeMessageReader.Message(2, "noreply@github.com", "New sign-in to your account"),
            FakeMessageReader.Message(3, "boss@example.com", "Quarterly numbers"),
            FakeMessageReader.Message(4, "boss@example.com", "Re: Quarterly numbers"),
            FakeMessageReader.Message(5, "news@other.example", "Weekly digest", newsletter: true),
            FakeMessageReader.Message(6, "old@friend.example", "Photos") with { ReceivedAt = new DateTimeOffset(2025, 1, 5, 10, 0, 0, TimeSpan.Zero) },
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var account = await db.MailAccounts.SingleAsync(a => a.Id == _accountId, Ct);
        account.FolderDelimiter = ".";
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static object Minimal => new
    {
        blueprintId = "minimal",
        folders = new Dictionary<string, string>
        {
            ["newsletters"] = "INBOX/Newsletters",
            ["notifications"] = "INBOX/Notifications",
            ["archive"] = "INBOX/Archive/{year}",
        },
        rules = new object[]
        {
            new { key = "newsletters", name = "Newsletters" },
            new { key = "notifications", name = "Notifications" },
            new { key = "archive", name = "Archive" },
        },
    };

    private static object Social(object? list = null) => new
    {
        blueprintId = "by-type",
        folders = new Dictionary<string, string> { ["social"] = "INBOX/Social", ["invoices"] = "INBOX/Finance/Invoices" },
        rules = new object[] { new { key = "invoices", name = "Invoices" }, new { key = "social", name = "Social networks" } },
        lists = new Dictionary<string, object> { ["social"] = list ?? new { name = "Social networks" } },
    };

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await _client.PostAsJsonAsync(url, body, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private Task<AssistantApplyResponse> ApplyAsync(object request) => PostAsync<AssistantApplyResponse>($"/api/accounts/{_accountId}/assistant/apply", request);

    private async Task<List<RuleResponse>> RulesAsync() => (await _client.GetFromJsonAsync<List<RuleResponse>>("/api/rules", Json, Ct))!;

    [Fact]
    public async Task Blueprints_list_their_folders_rules_and_unbound_parameters()
    {
        var blueprints = (await _client.GetFromJsonAsync<List<BlueprintInfo>>("/api/assistant/blueprints", Json, Ct))!;

        blueprints.Select(b => b.Id).ShouldBe(["minimal", "by-type", "business", "by-sender"]);
        var archive = blueprints[0].Rules.Single(r => r.Key == "archive");
        archive.Folder.ShouldBe("archive");
        archive.Parameters.Select(p => p.Name).ShouldBe(["days"]);
        blueprints.Single(b => b.Id == "by-type").Lists.Single().Entries.ShouldContain("linkedin.com");
    }

    [Fact]
    public async Task Analysis_counts_matches_conflicts_and_unsorted_senders_without_saving_anything()
    {
        (await _client.PostAsJsonAsync("/api/rules", new
        {
            accountId = _accountId,
            name = "Shop mail",
            conditions = new { kind = "condition", field = "FromDomain", @operator = "DomainMatches", value = "shop.example" },
            actions = new object[] { new { type = "MoveTo", folder = "INBOX/Shop" } },
        }, Ct)).EnsureSuccessStatusCode();

        var analysis = await PostAsync<AssistantAnalyzeResponse>($"/api/accounts/{_accountId}/assistant/analyze", new { selection = Minimal });

        analysis.Examined.ShouldBe(6);
        analysis.SortedByExistingRules.ShouldBe(1);
        analysis.Unsorted.ShouldBe(2);
        analysis.UnsortedDomains.ShouldBe([new SenderDomainCount("example.com", 2)]);

        var rules = analysis.Rules.ToDictionary(r => r.Key);
        rules.Keys.ShouldBe(["newsletters", "notifications", "archive"]);
        (rules["newsletters"].Matched, rules["newsletters"].Sorted, rules["newsletters"].Blocked).ShouldBe((2, 1, 1));
        rules["newsletters"].Examples.Single().ShouldBe(new AssistantExample("news@other.example", "Weekly digest", _reader.Folders["INBOX"][4].EffectiveDate, "INBOX.Newsletters"));
        rules["notifications"].Sorted.ShouldBe(1);
        rules["archive"].Examples.Single().TargetFolder.ShouldBe("INBOX.Archive.2025");

        (await RulesAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_creates_lists_and_dry_run_rules_after_the_existing_ones_and_the_folders()
    {
        (await _client.PostAsJsonAsync("/api/rules", new
        {
            accountId = _accountId,
            name = "Existing",
            conditions = new { kind = "condition", field = "IsFlagged", @operator = "IsTrue" },
            actions = new object[] { new { type = "MarkRead" } },
        }, Ct)).EnsureSuccessStatusCode();

        var result = await ApplyAsync(new { selection = Social(), createFolders = true });

        result.Rules.Select(r => r.Key).ShouldBe(["invoices", "social"]);
        result.Run.ShouldBeNull();
        result.Folders.ShouldBe([new AssistantFolderResult("INBOX.Social", true, null), new AssistantFolderResult("INBOX.Finance.Invoices", true, null)], ignoreOrder: true);
        _mail.HasFolder("INBOX.Finance.Invoices").ShouldBeTrue();

        var rules = await RulesAsync();
        rules.Select(r => (r.Name, r.Priority, r.DryRun)).ShouldBe([("Existing", 0, false), ("Invoices", 1, true), ("Social networks", 2, true)]);
        var listId = result.CreatedListIds.ShouldHaveSingleItem();
        rules[2].Conditions.ShouldBe(new Condition(ConditionField.FromDomain, ConditionOperator.InList, listId.ToString()));

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var list = await db.NamedLists.Include(l => l.Entries).SingleAsync(l => l.Id == listId, Ct);
        list.Name.ShouldBe("Social networks");
        list.Entries.Count.ShouldBe(FolderBlueprints.Seeds.Single(s => s.Key == "social").Entries.Count);
        (await db.RuleListReferences.CountAsync(r => r.ListId == listId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Apply_can_use_an_existing_list_and_queue_a_run_of_the_new_rules()
    {
        var created = await _client.PostAsJsonAsync("/api/lists", new { name = "Networks", kind = "Domain", entries = new[] { new { value = "xing.com" } } }, Ct);
        var listId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var result = await ApplyAsync(new { selection = Social(new { listId }), runOnInboxLimit = 50 });

        result.CreatedListIds.ShouldBeEmpty();
        await using var scope = _app.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Runs.SingleAsync(r => r.Id == result.Run!.Id, Ct);
        (run.Folder, run.Limit, run.State).ShouldBe(("INBOX", 50, RunState.Queued));
        run.RuleIds.ShouldBe(result.Rules.Select(r => r.Id).ToList());
    }

    [Fact]
    public async Task Invalid_selections_create_nothing()
    {
        var address = await _client.PostAsJsonAsync("/api/lists", new { name = "VIP", kind = "Address" }, Ct);
        var addressListId = (await address.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var selection = new
        {
            blueprintId = "by-type",
            folders = new Dictionary<string, string> { ["social"] = "INBOX/{nope}" },
            rules = new object[] { new { key = "social", name = "Social" }, new { key = "travel", name = "Travel" } },
            lists = new Dictionary<string, object> { ["social"] = new { listId = addressListId } },
        };

        var response = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/assistant/apply", new { selection }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.EnumerateObject().Select(e => e.Name).ShouldBe(["lists.social", "folders.social", "rules.travel.folder", "rules.social.list"], ignoreOrder: true);
        (await RulesAsync()).ShouldBeEmpty();
        (await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/assistant/apply", new { selection = new { blueprintId = "nope", folders = new { }, rules = Array.Empty<object>() } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_paused_account_cannot_queue_the_run()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
            (await db.MailAccounts.SingleAsync(a => a.Id == _accountId, Ct)).Enabled = false;
            await db.SaveChangesAsync(Ct);
        }

        var response = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/assistant/apply", new { selection = Minimal, runOnInboxLimit = 10 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RulesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Going_live_switches_dry_run_off_for_own_rules_only()
    {
        var result = await ApplyAsync(new { selection = Minimal });
        var ids = result.Rules.Select(r => r.Id).ToList();

        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();
        (await other.PostAsJsonAsync($"/api/accounts/{_accountId}/assistant/analyze", new { selection = Minimal }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync($"/api/accounts/{_accountId}/assistant/apply", new { selection = Minimal }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync("/api/rules/dry-run", new { ruleIds = ids, dryRun = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RulesAsync()).ShouldAllBe(r => r.DryRun);

        (await _client.PostAsJsonAsync("/api/rules/dry-run", new { ruleIds = ids, dryRun = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RulesAsync()).ShouldAllBe(r => !r.DryRun);
    }
}
