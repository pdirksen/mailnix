using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailnix.Api.Accounts;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Mailnix.Mail.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

public sealed class AccountEndpointsTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeImapServer _imap = new()
    {
        Folders =
        [
            new("INBOX", Messages: 10, Unread: 2),
            new("INBOX/Newsletters", Messages: 5),
            new("Trash", @"\Trash"),
            new("Junk", @"\Junk"),
        ],
    };

    private MailnixAppFactory _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = new MailnixAppFactory()
            .WithSetting("Mailnix:Mail:AllowInsecureConnections", "true")
            .WithSetting("Mailnix:Mail:ConnectTimeout", "00:00:05");
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _imap.DisposeAsync();
    }

    private object CreateRequest(string? password = null, string[]? watched = null) => new
    {
        name = "Work",
        host = "127.0.0.1",
        port = _imap.Port,
        security = "None",
        username = _imap.Username,
        password = password ?? _imap.Password,
        watchedFolders = watched ?? ["INBOX"],
    };

    private async Task<AccountResponse> CreateAccountAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", CreateRequest(), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<AccountResponse>(Json, Ct))!;
    }

    [Fact]
    public async Task Testing_unsaved_settings_returns_folders_and_special_folders()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts/test", new
        {
            host = "127.0.0.1", port = _imap.Port, security = "None", username = _imap.Username, password = _imap.Password,
        }, Ct);

        var result = await response.Content.ReadFromJsonAsync<ConnectionTestResponse>(Json, Ct);
        result!.Success.ShouldBeTrue();
        result.Mailbox!.Folders.Select(f => f.Path).ShouldBe(["INBOX", "INBOX/Newsletters", "Junk", "Trash"]);
        result.Mailbox.SpecialFolders.Trash.ShouldBe("Trash");
        result.Mailbox.Capabilities.Idle.ShouldBeTrue();
    }

    [Fact]
    public async Task Testing_with_a_wrong_password_reports_the_error_code()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts/test", new
        {
            host = "127.0.0.1", port = _imap.Port, security = "None", username = _imap.Username, password = "wrong",
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ConnectionTestResponse>(Json, Ct);
        result!.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe(ConnectionErrorCode.AuthenticationFailed);
    }

    [Fact]
    public async Task Creating_an_account_stores_detected_settings_and_an_encrypted_password()
    {
        var account = await CreateAccountAsync();

        account.Name.ShouldBe("Work");
        account.Status.ShouldBe(MailAccountStatus.Ok);
        account.WatchedFolders.ShouldBe(["INBOX"]);
        account.SpecialFolders.ShouldBe(new SpecialFolderMap(null, null, "Trash", "Junk", null));
        account.Capabilities.ShouldContain("Idle");

        var raw = await _client.GetStringAsync($"/api/accounts/{account.Id}", Ct);
        raw.ShouldNotContain(_imap.Password);
        raw.ShouldNotContain("secret", Case.Insensitive);

        await using var scope = _app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(Ct);
        stored.EncryptedSecret.ShouldNotContain(_imap.Password);
        scope.ServiceProvider.GetRequiredService<MailSecretProtector>().Unprotect(stored.EncryptedSecret).ShouldBe(_imap.Password);
    }

    [Fact]
    public async Task Creating_with_a_wrong_password_fails_with_the_error_code()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", CreateRequest(password: "wrong"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errorCode").GetString().ShouldBe("AuthenticationFailed");
        (await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts", Json, Ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Watched_folders_must_exist()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", CreateRequest(watched: ["INBOX", "Nope"]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Nope");
    }

    [Fact]
    public async Task Invalid_input_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", new
        {
            name = "", host = "127.0.0.1", port = 70000, security = "None", username = "u", password = "p", watchedFolders = Array.Empty<string>(),
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("Port");
        body.ShouldContain("WatchedFolders");
    }

    [Fact]
    public async Task Other_users_cannot_see_or_change_the_account()
    {
        var account = await CreateAccountAsync();
        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();

        (await other.GetFromJsonAsync<List<AccountResponse>>("/api/accounts", Json, Ct))!.ShouldBeEmpty();
        (await other.GetAsync($"/api/accounts/{account.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/accounts/{account.Id}/test", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/accounts/{account.Id}/folders", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"/api/accounts/{account.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/accounts/{account.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Updating_folders_without_a_new_password_reuses_the_stored_one()
    {
        var account = await CreateAccountAsync();

        var response = await _client.PutAsJsonAsync($"/api/accounts/{account.Id}", new
        {
            name = "Work mail",
            host = account.Host,
            port = account.Port,
            security = "None",
            username = account.Username,
            watchedFolders = new[] { "INBOX", "INBOX/Newsletters" },
            specialFolders = new { trash = "Trash", junk = (string?)null },
            enabled = true,
            blastRadiusLimit = 50,
            purgeTrashAfterDays = 30,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<AccountResponse>(Json, Ct))!;
        updated.Name.ShouldBe("Work mail");
        updated.WatchedFolders.ShouldBe(["INBOX", "INBOX/Newsletters"]);
        updated.SpecialFolders.Junk.ShouldBeNull();
        updated.BlastRadiusLimit.ShouldBe(50);
        updated.PurgeTrashAfterDays.ShouldBe(30);
    }

    [Fact]
    public async Task Renaming_does_not_need_the_server()
    {
        var account = await CreateAccountAsync();
        await _imap.DisposeAsync(); // server gone

        var response = await _client.PutAsJsonAsync($"/api/accounts/{account.Id}", new
        {
            name = "Renamed",
            host = account.Host,
            port = account.Port,
            security = "None",
            username = account.Username,
            watchedFolders = account.WatchedFolders,
            specialFolders = account.SpecialFolders,
            enabled = false,
            blastRadiusLimit = account.BlastRadiusLimit,
            purgeTrashAfterDays = (int?)null,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AccountResponse>(Json, Ct))!.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Testing_a_stored_account_records_failures()
    {
        var account = await CreateAccountAsync();
        await _imap.DisposeAsync();

        var result = await (await _client.PostAsync($"/api/accounts/{account.Id}/test", null, Ct))
            .Content.ReadFromJsonAsync<ConnectionTestResponse>(Json, Ct);

        result!.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe(ConnectionErrorCode.ConnectionRefused);
        var stored = await _client.GetFromJsonAsync<AccountResponse>($"/api/accounts/{account.Id}", Json, Ct);
        stored!.Status.ShouldBe(MailAccountStatus.Error);
        stored.LastErrorCode.ShouldBe(ConnectionErrorCode.ConnectionRefused);
        stored.LastCheckedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Folders_include_counts()
    {
        var account = await CreateAccountAsync();

        var folders = await _client.GetFromJsonAsync<FolderListResponse>($"/api/accounts/{account.Id}/folders", Json, Ct);

        folders!.Delimiter.ShouldBe("/");
        var inbox = folders.Folders.Single(f => f.Path == "INBOX");
        inbox.Messages.ShouldBe(10);
        inbox.Unread.ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_removes_the_account()
    {
        var account = await CreateAccountAsync();

        (await _client.DeleteAsync($"/api/accounts/{account.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync($"/api/accounts/{account.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accounts_require_authentication()
    {
        (await _app.CreateApiClient().GetAsync("/api/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

public sealed class AccountRaceTests
{
    [Fact]
    public async Task Deleting_an_account_while_its_folders_load_returns_not_found_instead_of_failing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var imap = new FakeImapServer { ListDelay = TimeSpan.FromMilliseconds(500) };
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:Mail:AllowInsecureConnections", "true");
        var client = app.CreateApiClient();
        (await client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        var created = await client.PostAsJsonAsync("/api/accounts", new
        {
            name = "Work", host = "127.0.0.1", port = imap.Port, security = "None", username = imap.Username, password = imap.Password,
            watchedFolders = new[] { "INBOX" },
        }, ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString();

        var folders = client.GetAsync($"/api/accounts/{id}/folders", ct);
        await imap.ListStarted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        // A second session (e.g. another browser tab) deletes the account meanwhile.
        var otherTab = app.CreateApiClient();
        (await otherTab.LoginAsync("owner@example.com")).EnsureSuccessStatusCode();
        (await otherTab.DeleteAsync($"/api/accounts/{id}", ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await folders).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

public sealed class AccountRateLimitTests
{
    [Fact]
    public async Task Connection_tests_are_rate_limited_per_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:RateLimits:MailConnectionsPerMinute", "2");
        var client = app.CreateApiClient();
        (await client.RegisterAsync("user@example.com")).EnsureSuccessStatusCode();
        var request = new { host = "does-not-exist.invalid", port = 993, security = "SslOnConnect", username = "u", password = "p" };

        (await client.PostAsJsonAsync("/api/accounts/test", request, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/accounts/test", request, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/accounts/test", request, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Limits are per user.
        var other = app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();
        (await other.PostAsJsonAsync("/api/accounts/test", request, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
