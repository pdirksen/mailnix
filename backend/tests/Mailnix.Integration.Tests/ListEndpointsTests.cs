using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mailnix.Api.Lists;
using Mailnix.Core.Rules;

namespace Mailnix.Integration.Tests;

public sealed class ListEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private readonly MailnixAppFactory _app = new();
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<ListResponse> CreateAsync(string kind, params object[] entries)
    {
        var response = await _client.PostAsJsonAsync("/api/lists", new { name = $"{kind} list", kind, entries }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ListResponse>(Json, Ct))!;
    }

    [Fact]
    public async Task Entries_are_normalized_and_deduplicated()
    {
        var list = await CreateAsync("Domain",
            new { value = "@Shop.Example", note = "main shop" },
            new { value = "shop.example" },
            new { value = "müller.de" });

        // Stored as punycode for matching, shown in Unicode.
        list.Entries.ShouldBe([new ListEntryDto("shop.example", "main shop"), new ListEntryDto("müller.de")]);

        var resaved = await _client.PutAsJsonAsync($"/api/lists/{list.Id}", new { name = list.Name, entries = list.Entries }, Ct);
        (await resaved.Content.ReadFromJsonAsync<ListResponse>(Json, Ct))!.Entries.ShouldBe(list.Entries);
    }

    [Fact]
    public async Task Invalid_entries_are_reported_per_entry()
    {
        var response = await _client.PostAsJsonAsync("/api/lists", new
        {
            name = "VIP",
            kind = "Address",
            entries = new object[] { new { value = "boss@example.com" }, new { value = "not an address" } },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("entries[1].value");
    }

    [Fact]
    public async Task Csv_import_appends_and_reports_problems_and_export_round_trips()
    {
        var list = await CreateAsync("Address", new { value = "boss@example.com", note = "Boss" });
        var csv = "value,note\nAlice@Example.org,\"Friend, old\"\nboss@example.com;dup\nnot-an-address\n# comment\n";

        var response = await _client.PostAsync($"/api/lists/{list.Id}/import", new StringContent(csv, Encoding.UTF8, "text/csv"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var result = await response.Content.ReadFromJsonAsync<ListImportResponse>(Json, Ct);

        result!.Added.ShouldBe(1);
        result.Duplicates.ShouldBe(1);
        result.Invalid.ShouldBe([new InvalidListEntry(4, "not-an-address", "Not an email address.")]);
        result.List.Entries.Select(e => e.Value).ShouldBe(["alice@example.org", "boss@example.com"]);

        var export = await _client.GetStringAsync($"/api/lists/{list.Id}/export", Ct);
        export.TrimStart('﻿').ShouldBe("value,note\r\nalice@example.org,\"Friend, old\"\r\nboss@example.com,Boss\r\n");
    }

    [Fact]
    public async Task Csv_export_defuses_spreadsheet_formulas()
    {
        var list = await CreateAsync("Address", new { value = "x@example.com", note = "=HYPERLINK(\"http://evil\")" });

        var export = await _client.GetStringAsync($"/api/lists/{list.Id}/export", Ct);

        export.ShouldContain("'=HYPERLINK");
    }

    [Fact]
    public async Task Lists_used_by_rules_cannot_be_deleted()
    {
        var list = await CreateAsync("Domain", new { value = "shop.example" });
        var accountId = await Seed.AccountAsync(_app, "owner@example.com");
        var rule = await _client.PostAsJsonAsync("/api/rules", new
        {
            accountId,
            name = "Shops",
            conditions = new { kind = "condition", field = "FromDomain", @operator = "InList", value = list.Id.ToString() },
            actions = new object[] { new { type = "Archive" } },
        }, Ct);
        rule.StatusCode.ShouldBe(HttpStatusCode.Created);

        var summaries = await _client.GetFromJsonAsync<List<ListSummaryResponse>>("/api/lists", Json, Ct);
        summaries!.Single().UsedByRuleCount.ShouldBe(1);
        summaries!.Single().EntryCount.ShouldBe(1);
        (await _client.DeleteAsync($"/api/lists/{list.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Other_users_cannot_access_lists()
    {
        var list = await CreateAsync("Domain", new { value = "shop.example" });
        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();

        (await other.GetAsync($"/api/lists/{list.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/lists/{list.Id}/export", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetFromJsonAsync<List<ListSummaryResponse>>("/api/lists", Json, Ct))!.ShouldBeEmpty();
    }
}
