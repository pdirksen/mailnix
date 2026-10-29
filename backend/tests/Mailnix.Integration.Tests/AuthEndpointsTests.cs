using System.Net;
using System.Net.Http.Json;
using Mailnix.Api.Auth;

namespace Mailnix.Integration.Tests;

public sealed class AuthEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_registered_user_becomes_admin_and_is_signed_in()
    {
        await using var app = new MailnixAppFactory();
        var client = app.CreateApiClient();

        var register = await client.RegisterAsync("first@example.com");

        register.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me", Ct);
        me.ShouldNotBeNull();
        me.Email.ShouldBe("first@example.com");
        me.DisplayName.ShouldBe("Test User");
        me.Roles.ShouldBe(["Admin", "TenantAdmin", "User"], ignoreOrder: true);
    }

    [Fact]
    public async Task Later_users_are_not_admins()
    {
        await using var app = new MailnixAppFactory();
        (await app.CreateApiClient().RegisterAsync("first@example.com")).EnsureSuccessStatusCode();
        var client = app.CreateApiClient();

        var me = await (await client.RegisterAsync("second@example.com")).Content.ReadFromJsonAsync<MeResponse>(Ct);

        me!.Roles.ShouldBe(["User"]);
    }

    [Fact]
    public async Task Simultaneous_first_sign_ups_make_only_one_administrator()
    {
        await using var app = new MailnixAppFactory();
        var clients = Enumerable.Range(0, 6).Select(_ => app.CreateApiClient()).ToList();

        var responses = await Task.WhenAll(clients.Select((client, i) => client.RegisterAsync($"user{i}@example.com")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var roles = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<MeResponse>(Ct))!.Roles));
        roles.Count(r => r.Contains("Admin")).ShouldBe(1);
    }

    [Fact]
    public async Task The_first_sign_up_needs_the_setup_code()
    {
        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Auth:RequireSetupCode", "true")
            .WithSetting("Mailnix:Auth:SetupCode", "ABCD-EFGH-JKMN");
        var client = app.CreateApiClient();
        (await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.SetupCodeRequired.ShouldBeTrue();

        async Task<HttpResponseMessage> SignUpAsync(string? code) =>
            await client.PostAsJsonAsync("/api/auth/register", new { email = "owner@example.com", password = HttpClientAuthExtensions.DefaultPassword, setupCode = code }, Ct);

        var missing = await SignUpAsync(null);
        missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await missing.Content.ReadAsStringAsync(Ct)).ShouldContain("SetupCodeInvalid");
        (await SignUpAsync("ABCD-EFGH-JKMP")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Typed without dashes and in lowercase is fine.
        (await SignUpAsync("abcdefghjkmn")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.SetupCodeRequired.ShouldBeFalse();
    }

    [Fact]
    public async Task Later_sign_ups_need_no_setup_code()
    {
        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Auth:RequireSetupCode", "true")
            .WithSetting("Mailnix:Auth:SetupCode", "ABCD-EFGH-JKMN");
        (await app.CreateApiClient().PostAsJsonAsync("/api/auth/register", new { email = "owner@example.com", password = HttpClientAuthExtensions.DefaultPassword, setupCode = "ABCD-EFGH-JKMN" }, Ct))
            .EnsureSuccessStatusCode();

        (await app.CreateApiClient().RegisterAsync("second@example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disabled_registration_still_allows_the_first_user_only()
    {
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:Auth:AllowRegistration", "false");
        var client = app.CreateApiClient();

        (await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.RegistrationOpen.ShouldBeTrue();
        (await client.RegisterAsync("first@example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var anonymous = app.CreateApiClient();
        (await anonymous.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.RegistrationOpen.ShouldBeFalse();
        (await anonymous.RegisterAsync("second@example.com")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Login_and_logout_round_trip()
    {
        await using var app = new MailnixAppFactory();
        (await app.CreateApiClient().RegisterAsync("user@example.com")).EnsureSuccessStatusCode();
        var client = app.CreateApiClient();

        (await client.LoginAsync("user@example.com", "wrong password!")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await client.LoginAsync("user@example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invalid_registration_returns_validation_problem()
    {
        await using var app = new MailnixAppFactory();
        var client = app.CreateApiClient();

        var tooShort = await client.RegisterAsync("user@example.com", password: "short");
        var badEmail = await client.RegisterAsync("not-an-email");

        tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badEmail.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await badEmail.Content.ReadAsStringAsync(Ct)).ShouldContain("Email");
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        await using var app = new MailnixAppFactory();
        (await app.CreateApiClient().RegisterAsync("user@example.com")).EnsureSuccessStatusCode();

        var duplicate = await app.CreateApiClient().RegisterAsync("user@example.com");

        duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bootstrap_admin_is_seeded_from_configuration()
    {
        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Bootstrap:AdminEmail", "admin@example.com")
            .WithSetting("Mailnix:Bootstrap:AdminPassword", "bootstrap password");
        var client = app.CreateApiClient();

        var login = await client.LoginAsync("admin@example.com", "bootstrap password");

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<MeResponse>(Ct))!.Roles.ShouldContain("Admin");

        // An existing admin means the next registration is an ordinary user.
        var next = await app.CreateApiClient().RegisterAsync("user@example.com");
        (await next.Content.ReadFromJsonAsync<MeResponse>(Ct))!.Roles.ShouldBe(["User"]);
    }

    [Fact]
    public async Task Auth_cookie_is_http_only_and_same_site_strict()
    {
        await using var app = new MailnixAppFactory();
        var client = app.CreateApiClient();

        var register = await client.RegisterAsync("user@example.com");

        var cookie = register.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("mailnix.auth=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
    }
}
