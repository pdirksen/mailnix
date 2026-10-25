using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailnix.Api.Accounts;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Mailnix.Mail.Tests.Fakes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Mailnix.Integration.Tests;

/// <summary>Adding and reconnecting Google/Microsoft mailboxes against a fake token endpoint and a fake IMAP server.</summary>
public sealed class OAuthTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeOAuthProvider _provider = new();

    private readonly FakeImapServer _imap = new()
    {
        Username = "user@example.com",
        AccessToken = "access-1",
        Capabilities = "IMAP4rev1 SPECIAL-USE IDLE MOVE UIDPLUS AUTH=XOAUTH2 SASL-IR",
        Folders =
        [
            new("INBOX"),
            new("[Gmail]", @"\Noselect"),
            new("[Gmail]/All Mail", @"\All"),
            new("[Gmail]/Trash", @"\Trash"),
            new("[Gmail]/Spam", @"\Junk"),
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
        foreach (var provider in new[] { "Google" })
        {
            _app.WithSetting($"Mailnix:OAuth:{provider}:ClientId", "test-client")
                .WithSetting($"Mailnix:OAuth:{provider}:ClientSecret", "test-secret")
                .WithSetting($"Mailnix:OAuth:{provider}:ImapHost", "127.0.0.1")
                .WithSetting($"Mailnix:OAuth:{provider}:ImapPort", _imap.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithSetting($"Mailnix:OAuth:{provider}:ImapSecurity", "None");
        }

        _app.WithServices(s => s.Replace(ServiceDescriptor.Singleton(sp => new OAuthClient(
            new HttpClient(_provider),
            sp.GetRequiredService<IOptions<OAuthOptions>>(),
            sp.GetRequiredService<TimeProvider>()))));
        _client = _app.CreateApiClient();
        (await _client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _imap.DisposeAsync();
    }

    private async Task<string> StartAsync(object? body = null, string provider = "google")
    {
        var response = await _client.PostAsJsonAsync($"/api/oauth/{provider}/start", body ?? new { name = "Gmail" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<StartOAuthResponse>(Json, Ct))!.AuthorizationUrl;
    }

    /// <summary>What the browser does: sign in at the provider, come back to /oauth/callback, which posts code and state.</summary>
    private async Task<HttpResponseMessage> SignInAsync(string authorizationUrl)
    {
        var state = QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query)["state"].ToString();
        return await _client.PostAsJsonAsync("/api/oauth/complete", new { state, code = _provider.Authorize(authorizationUrl) }, Ct);
    }

    private async Task<CompleteOAuthResponse> AddAccountAsync()
    {
        var response = await SignInAsync(await StartAsync());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CompleteOAuthResponse>(Json, Ct))!;
    }

    private async Task<string> StoredRefreshTokenAsync(Guid accountId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(a => a.Id == accountId, Ct);
        return scope.ServiceProvider.GetRequiredService<MailSecretProtector>().Unprotect(account.EncryptedSecret);
    }

    [Fact]
    public async Task Configured_providers_are_listed()
    {
        var providers = await _client.GetFromJsonAsync<List<OAuthProviderInfo>>("/api/oauth/providers", Json, Ct);

        providers!.ShouldBe([new(MailProvider.Google, true), new(MailProvider.Microsoft, false)]);
        (await _client.PostAsJsonAsync("/api/oauth/microsoft/start", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_authorization_url_asks_for_offline_mail_access_with_pkce()
    {
        var url = new Uri(await StartAsync());
        var query = QueryHelpers.ParseQuery(url.Query);

        url.GetLeftPart(UriPartial.Path).ShouldBe("https://accounts.google.com/o/oauth2/v2/auth");
        query["client_id"].ToString().ShouldBe("test-client");
        query["redirect_uri"].ToString().ShouldBe("http://localhost/oauth/callback");
        query["scope"].ToString().ShouldBe("https://mail.google.com/ openid email");
        query["code_challenge_method"].ToString().ShouldBe("S256");
        query["access_type"].ToString().ShouldBe("offline");
        query["prompt"].ToString().ShouldBe("consent");
        query["state"].ToString().Length.ShouldBeGreaterThan(50);
        query.ContainsKey("code_verifier").ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_in_adds_a_gmail_account_with_its_folders()
    {
        var result = await AddAccountAsync();

        result.Created.ShouldBeTrue();
        var account = result.Account;
        account.Provider.ShouldBe(MailProvider.Google);
        account.Name.ShouldBe("Gmail");
        account.Username.ShouldBe("user@example.com");
        account.Status.ShouldBe(MailAccountStatus.Ok);
        account.WatchedFolders.ShouldBe(["INBOX"]);
        account.SpecialFolders.Trash.ShouldBe("[Gmail]/Trash");
        account.SpecialFolders.Archive.ShouldBe("[Gmail]/All Mail");
        (await StoredRefreshTokenAsync(account.Id)).ShouldBe("refresh-1");

        // The code was exchanged with the verifier that belongs to the challenge (checked by the fake provider).
        _provider.Requests.Single()["grant_type"].ShouldBe("authorization_code");
        _provider.Requests.Single()["redirect_uri"].ShouldBe("http://localhost/oauth/callback");
    }

    [Fact]
    public async Task Access_tokens_are_refreshed_renewed_refresh_tokens_saved_and_revocation_reported()
    {
        _provider.ExpiresIn = 60; // too short to cache: every connection refreshes
        var account = (await AddAccountAsync()).Account;

        _provider.AccessToken = _imap.AccessToken = "access-2";
        _provider.RotatedRefreshToken = "refresh-2";
        var test = await _client.PostAsync($"/api/accounts/{account.Id}/test", null, Ct);
        (await test.Content.ReadFromJsonAsync<ConnectionTestResponse>(Json, Ct))!.Success.ShouldBeTrue();
        _provider.Requests.Last().ShouldSatisfyAllConditions(
            r => r["grant_type"].ShouldBe("refresh_token"),
            r => r["refresh_token"].ShouldBe("refresh-1"));
        (await StoredRefreshTokenAsync(account.Id)).ShouldBe("refresh-2");

        _provider.Revoked = true;
        var revoked = await (await _client.PostAsync($"/api/accounts/{account.Id}/test", null, Ct)).Content.ReadFromJsonAsync<ConnectionTestResponse>(Json, Ct);
        revoked!.Success.ShouldBeFalse();
        revoked.ErrorCode.ShouldBe(ConnectionErrorCode.ReconnectRequired);
        var stored = await _client.GetFromJsonAsync<AccountResponse>($"/api/accounts/{account.Id}", Json, Ct);
        stored!.LastErrorCode.ShouldBe(ConnectionErrorCode.ReconnectRequired);
    }

    [Fact]
    public async Task Reconnecting_replaces_the_grant_of_the_same_mailbox_only()
    {
        var account = (await AddAccountAsync()).Account;
        _provider.RefreshToken = "refresh-new";

        var url = await StartAsync(new { accountId = account.Id });
        QueryHelpers.ParseQuery(new Uri(url).Query)["login_hint"].ToString().ShouldBe("user@example.com");
        var response = await SignInAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CompleteOAuthResponse>(Json, Ct))!.Created.ShouldBeFalse();
        (await StoredRefreshTokenAsync(account.Id)).ShouldBe("refresh-new");

        // Signing in to another mailbox does not take over this account.
        _provider.Email = _imap.Username = "someone-else@example.com";
        var mismatch = await SignInAsync(await StartAsync(new { accountId = account.Id }));
        mismatch.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await mismatch.Content.ReadAsStringAsync(Ct)).ShouldContain("OAuthDifferentMailbox");
        (await StoredRefreshTokenAsync(account.Id)).ShouldBe("refresh-new");
    }

    [Fact]
    public async Task The_mailbox_limit_of_the_organization_applies_to_new_but_not_to_reconnected_accounts()
    {
        var first = await AddAccountAsync();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Tenants.ExecuteUpdateAsync(u => u.SetProperty(t => t.MaxAccounts, 1), Ct);
        }

        var second = await SignInAsync(await StartAsync());
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldContain("AccountLimitReached");

        (await SignInAsync(await StartAsync(new { accountId = first.Account.Id }))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tampered_or_foreign_state_is_rejected()
    {
        var url = await StartAsync();
        var state = QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();
        var code = _provider.Authorize(url);

        var tampered = await _client.PostAsJsonAsync("/api/oauth/complete", new { state = state[..^4] + "AAAA", code }, Ct);
        tampered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();
        var foreign = await other.PostAsJsonAsync("/api/oauth/complete", new { state, code }, Ct);
        foreign.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await foreign.Content.ReadAsStringAsync(Ct)).ShouldContain("OAuthStateInvalid");
        _provider.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_offline_access_is_explained()
    {
        _provider.WithoutRefreshToken = true;

        var response = await SignInAsync(await StartAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("OAuthNoRefreshToken");
    }

    [Fact]
    public async Task Server_and_user_of_oauth_accounts_cannot_be_changed_in_the_settings()
    {
        var account = (await AddAccountAsync()).Account;

        var response = await _client.PutAsJsonAsync($"/api/accounts/{account.Id}", new
        {
            name = "Renamed",
            host = "evil.example.com",
            port = 993,
            security = "SslOnConnect",
            username = "attacker@example.com",
            password = "ignored",
            watchedFolders = account.WatchedFolders,
            specialFolders = account.SpecialFolders,
            enabled = true,
            blastRadiusLimit = 200,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var updated = (await response.Content.ReadFromJsonAsync<AccountResponse>(Json, Ct))!;
        updated.Name.ShouldBe("Renamed");
        updated.Host.ShouldBe("127.0.0.1");
        updated.Username.ShouldBe("user@example.com");
        (await StoredRefreshTokenAsync(account.Id)).ShouldBe("refresh-1");
    }
}
