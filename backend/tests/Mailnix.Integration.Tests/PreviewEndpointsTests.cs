using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Rules;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

public sealed class PreviewEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly FakeMessageReader _reader = new();
    private readonly MailnixAppFactory _app;
    private HttpClient _client = null!;
    private Guid _accountId;

    public PreviewEndpointsTests()
    {
        _app = new MailnixAppFactory().WithServices(s => s.Replace(ServiceDescriptor.Singleton<IMessageReader>(_reader)));
        _reader.Folders["INBOX"].AddRange(
        [
            FakeMessageReader.Message(1, "news@shop.example", "Autumn sale", newsletter: true),
            FakeMessageReader.Message(2, "boss@example.com", "Quarterly numbers"),
            FakeMessageReader.Message(3, "news@other.example", "Weekly digest", newsletter: true, seen: true),
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
        await ConfigureAccountAsync(a =>
        {
            a.FolderDelimiter = ".";
            a.SpecialFolders = new MailAccountSpecialFolders { Trash = "INBOX.Trash", Archive = "INBOX.Archive" };
        });
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task ConfigureAccountAsync(Action<MailAccount> change)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        change(await db.MailAccounts.SingleAsync(a => a.Id == _accountId));
        await db.SaveChangesAsync();
    }

    private static readonly object NewsletterCondition = new { kind = "condition", field = "IsNewsletter", @operator = "IsTrue" };

    private async Task<PreviewResponse> PreviewAsync(object request)
    {
        var response = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/rules/preview", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PreviewResponse>(Json, Ct))!;
    }

    [Fact]
    public async Task Draft_rule_is_explained_per_message_without_saving_anything()
    {
        var preview = await PreviewAsync(new
        {
            name = "Newsletters",
            conditions = NewsletterCondition,
            actions = new object[] { new { type = "MarkRead" }, new { type = "MoveTo", folder = "INBOX/Newsletters/{year}" } },
        });

        preview.Folder.ShouldBe("INBOX");
        preview.Examined.ShouldBe(3);
        preview.Matched.ShouldBe(2);
        preview.TerminalActions.ShouldBe(2);
        preview.Messages.Select(m => m.Uid).ShouldBe([3u, 2u, 1u]);

        var sale = preview.Messages.Single(m => m.Uid == 1);
        sale.Matched.ShouldBeTrue();
        sale.Trace!.Result.ShouldBeTrue();
        sale.Actions.Select(a => (a.Type, a.TargetFolder)).ShouldBe([(ActionType.MarkRead, null), (ActionType.MoveTo, "INBOX.Newsletters.2026")]);

        // Already read: marking it read again is reported as no change.
        var digest = preview.Messages.Single(m => m.Uid == 3);
        digest.Skipped.Single().Reason.ShouldBe(SkipReason.NoChange);

        preview.Messages.Single(m => m.Uid == 2).Matched.ShouldBeFalse();
        (await _client.GetFromJsonAsync<List<RuleResponse>>("/api/rules", Json, Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Conflicts_with_higher_priority_rules_are_shown()
    {
        var created = await _client.PostAsJsonAsync("/api/rules", new
        {
            accountId = _accountId,
            name = "Archive shop mail",
            conditions = new { kind = "condition", field = "FromDomain", @operator = "DomainMatches", value = "shop.example" },
            actions = new object[] { new { type = "Archive" } },
        }, Ct);
        created.EnsureSuccessStatusCode();

        var preview = await PreviewAsync(new { conditions = NewsletterCondition, actions = new object[] { new { type = "Delete" } } });

        var sale = preview.Messages.Single(m => m.Uid == 1);
        sale.Actions.ShouldBeEmpty();
        sale.Skipped.Single().ShouldBe(new PreviewSkippedAction(ActionType.Delete, "INBOX.Trash", SkipReason.TerminalActionAlreadyPlanned, "Archive shop mail"));
        sale.OtherRulesActions.Single().RuleName.ShouldBe("Archive shop mail");
        preview.Messages.Single(m => m.Uid == 3).Actions.Single().TargetFolder.ShouldBe("INBOX.Trash");
    }

    [Fact]
    public async Task Editing_a_saved_rule_replaces_it_in_the_preview()
    {
        var created = await _client.PostAsJsonAsync("/api/rules", new
        {
            accountId = _accountId,
            name = "Flag newsletters",
            conditions = NewsletterCondition,
            actions = new object[] { new { type = "Flag" } },
        }, Ct);
        var rule = (await created.Content.ReadFromJsonAsync<RuleResponse>(Json, Ct))!;

        var preview = await PreviewAsync(new { ruleId = rule.Id, conditions = NewsletterCondition, actions = new object[] { new { type = "MarkRead" } } });

        // Only the draft's actions; the saved version is not evaluated alongside.
        var sale = preview.Messages.Single(m => m.Uid == 1);
        sale.Actions.Select(a => a.Type).ShouldBe([ActionType.MarkRead]);
        sale.OtherRulesActions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Messages_are_cached_until_refresh()
    {
        var request = new { conditions = NewsletterCondition, actions = Array.Empty<object>() };

        var first = await PreviewAsync(request);
        var second = await PreviewAsync(request);
        var refreshed = await PreviewAsync(new { conditions = NewsletterCondition, actions = Array.Empty<object>(), refresh = true });

        first.FromCache.ShouldBeFalse();
        second.FromCache.ShouldBeTrue();
        refreshed.FromCache.ShouldBeFalse();
        _reader.Reads.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Only_needed_data_is_fetched_and_body_previews_are_smaller()
    {
        await PreviewAsync(new { conditions = new { kind = "condition", field = "Subject", @operator = "Contains", value = "sale" }, actions = Array.Empty<object>(), limit = 300 });
        var body = await PreviewAsync(new { conditions = new { kind = "condition", field = "Body", @operator = "Contains", value = "x" }, actions = Array.Empty<object>(), limit = 300 });

        _reader.Reads[0].ShouldBe(("INBOX", 300, FetchRequirements.None));
        _reader.Reads[1].Requirements.ShouldBe(FetchRequirements.Body);
        body.Limit.ShouldBe(PreviewEndpoints.MaxMessagesWithBody);
    }

    [Fact]
    public async Task Exceeding_the_confirmation_threshold_is_flagged()
    {
        await ConfigureAccountAsync(a => a.BlastRadiusLimit = 1);

        var preview = await PreviewAsync(new { conditions = NewsletterCondition, actions = new object[] { new { type = "Archive" } } });

        preview.TerminalActions.ShouldBe(2);
        preview.ConfirmationThreshold.ShouldBe(1);
        preview.ExceedsConfirmationThreshold.ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_drafts_unknown_folders_and_foreign_accounts_are_rejected()
    {
        var invalid = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/rules/preview",
            new { conditions = new { kind = "condition", field = "Size", @operator = "GreaterThan", value = "big" }, actions = Array.Empty<object>() }, Ct);
        var missingFolder = await _client.PostAsJsonAsync($"/api/accounts/{_accountId}/rules/preview",
            new { conditions = NewsletterCondition, actions = Array.Empty<object>(), folder = "Nope" }, Ct);
        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();
        var foreign = await other.PostAsJsonAsync($"/api/accounts/{_accountId}/rules/preview",
            new { conditions = NewsletterCondition, actions = Array.Empty<object>() }, Ct);

        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missingFolder.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missingFolder.Content.ReadAsStringAsync(Ct)).ShouldContain("Nope");
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Templates_produce_unsaved_drafts()
    {
        var templates = await _client.GetFromJsonAsync<List<RuleTemplateInfo>>("/api/rule-templates", Json, Ct);
        templates!.Select(t => t.Id).ShouldContain("newsletter-cleanup");

        var response = await _client.PostAsJsonAsync("/api/rule-templates/newsletter-cleanup/draft",
            new { name = "Newsletter cleanup", parameters = new Dictionary<string, string> { ["days"] = "14" } }, Ct);
        var draft = (await response.Content.ReadFromJsonAsync<RuleDraftResponse>(Json, Ct))!;

        draft.Trigger.ShouldBe(RuleTrigger.Scheduled);
        draft.Schedule.ShouldBe("0 3 * * *");
        draft.Conditions.ShouldBeOfType<ConditionGroup>().Children.ShouldContain(new Condition(ConditionField.AgeDays, ConditionOperator.GreaterThan, "14"));
        draft.Actions.ShouldContain(new MoveToAction("INBOX/Archive/Newsletters/{year}"));

        var missingList = await _client.PostAsJsonAsync("/api/rule-templates/sort-by-sender-domain/draft", new { name = "x" }, Ct);
        missingList.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/rule-templates/nope/draft", new { name = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
