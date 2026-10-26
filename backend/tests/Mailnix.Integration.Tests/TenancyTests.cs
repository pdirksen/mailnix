using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailnix.Api.Auth;
using Mailnix.Api.Team;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

/// <summary>SaaS mode: every signup founds its own organization (tenant); data never crosses tenants.</summary>
public sealed class TenancyTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // Roles are reloaded into the cookie on every request here instead of once a minute.
    private readonly MailnixAppFactory _app = new MailnixAppFactory()
        .WithSetting("Mailnix:Tenancy:Mode", "MultiTenant")
        .WithServices(s => s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero));
    private HttpClient _operator = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _operator = _app.CreateApiClient();
        (await _operator.RegisterAsync("operator@example.com")).EnsureSuccessStatusCode();
        _alice = _app.CreateApiClient();
        (await _alice.PostAsJsonAsync("/api/auth/register", new { email = "alice@acme.test", password = HttpClientAuthExtensions.DefaultPassword, organizationName = "Acme" }, Ct)).EnsureSuccessStatusCode();
        _bob = _app.CreateApiClient();
        (await _bob.RegisterAsync("bob@globex.test")).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private static async Task<CreatedInvitation> InviteAsync(HttpClient client, string? email = null)
    {
        var response = await client.PostAsJsonAsync("/api/team/invitations", new { email }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CreatedInvitation>(Json, Ct))!;
    }

    private static string TokenOf(CreatedInvitation invitation) => QueryHelpers.ParseQuery(new Uri(invitation.Url).Query)["invite"].ToString();

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string email, string token) =>
        client.PostAsJsonAsync("/api/auth/register", new { email, password = HttpClientAuthExtensions.DefaultPassword, inviteToken = token }, Ct);

    private async Task<Guid> TenantOfAsync(string email)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Users.Where(u => u.Email == email).Select(u => u.TenantId).SingleAsync(Ct);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("errorCode", out var code) ? code.GetString() : null;

    [Fact]
    public async Task Each_signup_founds_its_own_organization_and_administers_it()
    {
        (await GetAsync<AuthConfigResponse>(_alice, "/api/auth/config")).MultiTenant.ShouldBeTrue();

        var alice = await GetAsync<MeResponse>(_alice, "/api/auth/me");
        alice.Roles.ShouldBe(["TenantAdmin", "User"], ignoreOrder: true);
        (await GetAsync<TeamResponse>(_alice, "/api/team")).Tenant.Name.ShouldBe("Acme");
        (await GetAsync<TeamResponse>(_bob, "/api/team")).Tenant.Name.ShouldBe("bob@globex.test", "without an organization name, the email names it");

        var tenants = new[] { await TenantOfAsync("operator@example.com"), await TenantOfAsync("alice@acme.test"), await TenantOfAsync("bob@globex.test") };
        tenants.Distinct().Count().ShouldBe(3);
        tenants[0].ShouldBe(Tenant.DefaultId, "the operator keeps the default tenant");
    }

    [Fact]
    public async Task Mailboxes_and_users_of_other_organizations_stay_invisible()
    {
        var aliceAccount = await Seed.AccountAsync(_app, "alice@acme.test");

        (await _bob.GetAsync($"/api/accounts/{aliceAccount}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _bob.DeleteAsync($"/api/accounts/{aliceAccount}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync<TeamResponse>(_bob, "/api/team")).Users.Select(u => u.Email).ShouldBe(["bob@globex.test"]);

        // The query filter itself, for code that forgets to filter by user.
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        db.TenantFilter = await TenantOfAsync("bob@globex.test");
        (await db.MailAccounts.AnyAsync(Ct)).ShouldBeFalse();
        db.TenantFilter = await TenantOfAsync("alice@acme.test");
        (await db.MailAccounts.Select(a => a.Id).ToListAsync(Ct)).ShouldBe([aliceAccount]);
    }

    [Fact]
    public async Task Invited_users_join_the_inviting_organization_once()
    {
        var invitation = await InviteAsync(_alice, "carol@acme.test");
        invitation.Url.ShouldStartWith("http://localhost/register?invite=");
        var token = TokenOf(invitation);

        var info = await GetAsync<InvitationInfo>(_app.CreateApiClient(), $"/api/auth/invitations/{token}");
        info.ShouldBe(new InvitationInfo("Acme", "carol@acme.test"));

        var stranger = await AcceptAsync(_app.CreateApiClient(), "mallory@evil.test", token);
        stranger.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(stranger)).ShouldBe("InvitationForOtherEmail");

        var carol = _app.CreateApiClient();
        (await AcceptAsync(carol, "Carol@Acme.test", token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync<MeResponse>(carol, "/api/auth/me")).Roles.ShouldBe(["User"]);
        (await TenantOfAsync("Carol@Acme.test")).ShouldBe(await TenantOfAsync("alice@acme.test"));
        (await carol.GetAsync("/api/team", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "only organization admins manage the team");

        var again = await AcceptAsync(_app.CreateApiClient(), "carol2@acme.test", token);
        (await ErrorCodeAsync(again)).ShouldBe("InvitationInvalid");
        (await GetAsync<TeamResponse>(_alice, "/api/team")).Invitations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Expired_and_revoked_invitations_are_refused()
    {
        var expired = await InviteAsync(_alice);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Invitations
                .Where(i => i.Id == expired.Invitation.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
        }

        (await _app.CreateApiClient().GetAsync($"/api/auth/invitations/{TokenOf(expired)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(await AcceptAsync(_app.CreateApiClient(), "late@acme.test", TokenOf(expired)))).ShouldBe("InvitationInvalid");

        var revoked = await InviteAsync(_alice);
        (await _bob.DeleteAsync($"/api/team/invitations/{revoked.Invitation.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "not Bob's invitation");
        (await _alice.DeleteAsync($"/api/team/invitations/{revoked.Invitation.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ErrorCodeAsync(await AcceptAsync(_app.CreateApiClient(), "dave@acme.test", TokenOf(revoked)))).ShouldBe("InvitationInvalid");
    }

    [Fact]
    public async Task The_last_organization_admin_stays_and_removed_users_take_their_mailboxes_along()
    {
        var carol = _app.CreateApiClient();
        (await AcceptAsync(carol, "carol@acme.test", TokenOf(await InviteAsync(_alice)))).EnsureSuccessStatusCode();
        var carolAccount = await Seed.AccountAsync(_app, "carol@acme.test");
        var team = await GetAsync<TeamResponse>(_alice, "/api/team");
        var aliceId = team.Users.Single(u => u.Email == "alice@acme.test").Id;
        var carolId = team.Users.Single(u => u.Email == "carol@acme.test").Id;
        team.Tenant.Accounts.ShouldBe(1);

        var demote = await _alice.PutAsJsonAsync($"/api/team/users/{aliceId}/admin", new { isTenantAdmin = false }, Ct);
        demote.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(demote)).ShouldBe("LastTenantAdmin");

        // With a second admin, the first may step down.
        (await _alice.PutAsJsonAsync($"/api/team/users/{carolId}/admin", new { isTenantAdmin = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _alice.PutAsJsonAsync($"/api/team/users/{aliceId}/admin", new { isTenantAdmin = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _alice.GetAsync("/api/team", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the cookie follows the role change");

        (await ErrorCodeAsync(await carol.DeleteAsync($"/api/team/users/{carolId}", Ct))).ShouldBe("CannotRemoveSelf");
        (await _bob.DeleteAsync($"/api/team/users/{aliceId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "another organization");

        (await carol.PutAsJsonAsync($"/api/team/users/{aliceId}/admin", new { isTenantAdmin = true }, Ct)).EnsureSuccessStatusCode();
        (await _alice.DeleteAsync($"/api/team/users/{carolId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await carol.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = _app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.IgnoreQueryFilters().AnyAsync(a => a.Id == carolAccount, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Founders_hand_over_before_leaving_and_empty_organizations_disappear()
    {
        var carol = _app.CreateApiClient();
        (await AcceptAsync(carol, "carol@acme.test", TokenOf(await InviteAsync(_alice)))).EnsureSuccessStatusCode();
        var acme = await TenantOfAsync("alice@acme.test");
        var password = new { password = HttpClientAuthExtensions.DefaultPassword };

        (await ErrorCodeAsync(await _alice.PostAsJsonAsync("/api/profile/delete", password, Ct))).ShouldBe("LastTenantAdmin");

        // Members leave freely; the founder, once alone, takes the organization along.
        (await carol.PostAsJsonAsync("/api/profile/delete", password, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _alice.PostAsJsonAsync("/api/profile/delete", password, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = _app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Tenants.AnyAsync(t => t.Id == acme, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Organizations_can_be_renamed_by_their_admins()
    {
        (await _alice.PutAsJsonAsync("/api/team", new { name = "  Acme Corp " }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync<TeamResponse>(_alice, "/api/team")).Tenant.Name.ShouldBe("Acme Corp");
        (await GetAsync<TeamResponse>(_bob, "/api/team")).Tenant.Name.ShouldBe("bob@globex.test");
    }

    [Fact]
    public async Task The_operator_sees_all_organizations_and_limits_their_mailboxes()
    {
        (await _alice.GetAsync("/api/system", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Seed.AccountAsync(_app, "alice@acme.test");

        var system = await GetAsync<SystemResponse>(_operator, "/api/system");
        system.Version.ShouldStartWith("0.0.0-dev", Case.Sensitive, "tests run on development builds");
        system.MultiTenant.ShouldBeTrue();
        system.Accounts.ShouldBe(1);
        system.UnleasedAccounts.ShouldBe(1, "no worker runs in this test");
        system.Tenants.Select(t => (t.Name, t.Users, t.Accounts)).ShouldBe([("Acme", 1, 1), ("bob@globex.test", 1, 0), ("Default", 1, 0)], ignoreOrder: true);

        var acme = system.Tenants.Single(t => t.Name == "Acme").Id;
        (await _operator.PutAsJsonAsync($"/api/system/tenants/{acme}", new { maxAccounts = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _operator.PutAsJsonAsync($"/api/system/tenants/{Guid.NewGuid()}", new { maxAccounts = 1 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The limit applies before any connection to the mail server is made.
        var full = await _alice.PostAsJsonAsync("/api/accounts", new
        {
            name = "Second", host = "imap.example.com", port = 993, security = "SslOnConnect", username = "a", password = "b", watchedFolders = new[] { "INBOX" },
        }, Ct);
        full.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(full)).ShouldBe("AccountLimitReached");
        (await GetAsync<TeamResponse>(_alice, "/api/team")).Tenant.MaxAccounts.ShouldBe(1);
    }
}

/// <summary>Self-hosted with signup closed: invitations are the way in, into the one organization.</summary>
public sealed class ClosedSignupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Invited_users_can_join_although_signup_is_closed()
    {
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:Auth:AllowRegistration", "false");
        var admin = app.CreateApiClient();
        (await admin.RegisterAsync("admin@example.com")).EnsureSuccessStatusCode();
        (await app.CreateApiClient().RegisterAsync("uninvited@example.com")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var response = await admin.PostAsJsonAsync("/api/team/invitations", new { }, Ct);
        var invitation = (await response.Content.ReadFromJsonAsync<CreatedInvitation>(Ct))!;
        invitation.Invitation.Email.ShouldBeNull("anyone with the link");
        var token = QueryHelpers.ParseQuery(new Uri(invitation.Url).Query)["invite"].ToString();

        var guest = app.CreateApiClient();
        (await guest.PostAsJsonAsync("/api/auth/register", new { email = "guest@example.com", password = HttpClientAuthExtensions.DefaultPassword, inviteToken = token }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await guest.GetFromJsonAsync<MeResponse>("/api/auth/me", Ct))!.Roles.ShouldBe(["User"]);

        await using var scope = app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Users.Select(u => u.TenantId).Distinct().ToListAsync(Ct)).ShouldBe([Tenant.DefaultId]);
    }
}
