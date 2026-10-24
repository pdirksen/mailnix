using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Rules;
using Mailnix.Core.Rules;

namespace Mailnix.Integration.Tests;

public sealed class RuleEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly MailnixAppFactory _app = new();
    private HttpClient _client = null!;
    private Guid _accountId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static object Newsletter(Guid accountId, string name = "Newsletters", object? conditions = null, object[]? actions = null) => new
    {
        accountId,
        name,
        conditions = conditions ?? new { kind = "condition", field = "IsNewsletter", @operator = "IsTrue" },
        actions = actions ?? [new { type = "MarkRead" }, new { type = "MoveTo", folder = "INBOX/Newsletters/{year}" }],
    };

    private async Task<RuleResponse> CreateAsync(object request)
    {
        var response = await _client.PostAsJsonAsync("/api/rules", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RuleResponse>(Json, Ct))!;
    }

    private static object Reference(Guid ruleId, string op = "Matches") => new { kind = "condition", field = "Rule", @operator = op, value = ruleId.ToString() };

    [Fact]
    public async Task Rules_are_created_with_increasing_priority_and_round_trip_their_definition()
    {
        var first = await CreateAsync(Newsletter(_accountId));
        var second = await CreateAsync(Newsletter(_accountId, "Second"));

        first.Priority.ShouldBe(0);
        second.Priority.ShouldBe(1);
        first.Conditions.ShouldBe(new Condition(ConditionField.IsNewsletter, ConditionOperator.IsTrue));
        first.Actions.ShouldBe([new MarkReadAction(), new MoveToAction("INBOX/Newsletters/{year}")]);

        var listed = await _client.GetFromJsonAsync<List<RuleResponse>>($"/api/rules?accountId={_accountId}", Json, Ct);
        listed!.Select(r => r.Name).ShouldBe(["Newsletters", "Second"]);
    }

    [Fact]
    public async Task Invalid_rules_are_rejected_with_paths()
    {
        var response = await _client.PostAsJsonAsync("/api/rules", Newsletter(
            _accountId,
            conditions: new
            {
                kind = "group",
                @operator = "All",
                children = new object[] { new { kind = "condition", field = "Size", @operator = "GreaterThan", value = "big" } },
            },
            actions: [new { type = "MoveTo", folder = "Archive/{nope}" }]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("conditions.children[0].value", out _).ShouldBeTrue();
        errors.TryGetProperty("actions[0].folder", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Scheduled_rules_need_a_valid_cron_expression()
    {
        var request = new
        {
            accountId = _accountId,
            name = "Cleanup",
            trigger = "Scheduled",
            schedule = "every night",
            conditions = new { kind = "condition", field = "AgeDays", @operator = "GreaterThan", value = "30" },
            actions = new object[] { new { type = "Delete" } },
        };

        var response = await _client.PostAsJsonAsync("/api/rules", request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("cron");
    }

    [Fact]
    public async Task Referenced_rules_cannot_be_deleted_and_cycles_are_rejected()
    {
        var filter = await CreateAsync(Newsletter(_accountId, "Filter", actions: []));
        var user = await CreateAsync(Newsletter(_accountId, "Uses filter", conditions: Reference(filter.Id)));

        var delete = await _client.DeleteAsync($"/api/rules/{filter.Id}", Ct);
        delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await delete.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("dependents")[0].GetProperty("name").GetString().ShouldBe("Uses filter");

        var fetched = await _client.GetFromJsonAsync<RuleResponse>($"/api/rules/{filter.Id}", Json, Ct);
        fetched!.UsedBy.Select(r => r.Name).ShouldBe(["Uses filter"]);

        // Filter → Uses filter → Filter would be a cycle.
        var cycle = await _client.PutAsJsonAsync($"/api/rules/{filter.Id}", new
        {
            name = "Filter",
            conditions = Reference(user.Id),
            actions = Array.Empty<object>(),
        }, Ct);
        cycle.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await cycle.Content.ReadAsStringAsync(Ct)).ShouldContain("cycle");

        // Once nothing references it any more, it can be deleted.
        (await _client.DeleteAsync($"/api/rules/{user.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.DeleteAsync($"/api/rules/{filter.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Rules_can_be_reordered()
    {
        var a = await CreateAsync(Newsletter(_accountId, "A"));
        var b = await CreateAsync(Newsletter(_accountId, "B"));
        var c = await CreateAsync(Newsletter(_accountId, "C"));

        var bad = await _client.PostAsJsonAsync("/api/rules/reorder", new { accountId = _accountId, ruleIds = new[] { c.Id, a.Id } }, Ct);
        var good = await _client.PostAsJsonAsync("/api/rules/reorder", new { accountId = _accountId, ruleIds = new[] { c.Id, a.Id, b.Id } }, Ct);

        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        good.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var listed = await _client.GetFromJsonAsync<List<RuleResponse>>($"/api/rules?accountId={_accountId}", Json, Ct);
        listed!.Select(r => (r.Name, r.Priority)).ShouldBe([("C", 0), ("A", 1), ("B", 2)]);
    }

    [Fact]
    public async Task Drafts_can_be_validated_without_saving()
    {
        var response = await _client.PostAsJsonAsync("/api/rules/validate", new
        {
            accountId = _accountId,
            conditions = new { kind = "condition", field = "Subject", @operator = "Regex", value = "(unclosed" },
            actions = new object[] { new { type = "Delete" }, new { type = "Archive" } },
        }, Ct);

        var result = await response.Content.ReadFromJsonAsync<RuleValidationResponse>(Json, Ct);
        result!.Valid.ShouldBeFalse();
        result.Errors.Select(e => e.Code).ShouldBe(["InvalidRegex", "MultipleTerminalActions"], ignoreOrder: true);
        (await _client.GetFromJsonAsync<List<RuleResponse>>("/api/rules", Json, Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_users_cannot_see_use_or_reference_rules()
    {
        var rule = await CreateAsync(Newsletter(_accountId));
        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();
        var otherAccount = await Seed.AccountAsync(_app, "other@example.com");

        (await other.GetAsync($"/api/rules/{rule.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"/api/rules/{rule.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync("/api/rules", Newsletter(_accountId), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var reference = await other.PostAsJsonAsync("/api/rules", Newsletter(otherAccount, conditions: Reference(rule.Id)), Ct);
        reference.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reference.Content.ReadAsStringAsync(Ct)).ShouldContain("does not exist");
    }

    [Fact]
    public async Task Deleting_an_account_deletes_its_rules_unless_another_account_depends_on_them()
    {
        var secondAccount = await Seed.AccountAsync(_app, "owner@example.com", "Private");
        var filter = await CreateAsync(Newsletter(_accountId, "Filter", actions: []));
        var dependent = await CreateAsync(Newsletter(secondAccount, "Dependent", conditions: Reference(filter.Id)));

        (await _client.DeleteAsync($"/api/accounts/{_accountId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await _client.DeleteAsync($"/api/rules/{dependent.Id}", Ct)).EnsureSuccessStatusCode();
        (await _client.DeleteAsync($"/api/accounts/{_accountId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/rules/{filter.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Field_catalog_describes_fields_and_placeholders()
    {
        var catalog = await _client.GetFromJsonAsync<FieldCatalogResponse>("/api/rules/field-catalog", Json, Ct);

        catalog!.Fields.Single(f => f.Field == ConditionField.DmarcResult).Options!.ShouldContain("fail");
        catalog.Fields.Single(f => f.Field == ConditionField.FromDomain).Operators.ShouldContain(ConditionOperator.DomainMatches);
        catalog.FolderPlaceholders.ShouldContain("fromDomain");
    }
}
