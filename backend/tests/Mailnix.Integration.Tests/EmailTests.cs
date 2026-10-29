using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Auth;
using Mailnix.Api.Profile;
using Mailnix.Api.Team;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

public sealed class EmailTests : IAsyncLifetime
{
    private const string PublicUrl = "https://mail.example.org";

    private readonly FakeEmailSender _email = new();
    private MailnixAppFactory _app = null!;
    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = Create(publicUrl: PublicUrl);
        _admin = _app.CreateApiClient();
        (await _admin.RegisterAsync("admin@example.com")).EnsureSuccessStatusCode();
        _user = _app.CreateApiClient();
        (await _user.RegisterAsync("user@example.com")).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    // Cookies are checked against the database on every request, so "signed out elsewhere" shows at once.
    private MailnixAppFactory Create(string? publicUrl) => new MailnixAppFactory()
        .WithSetting("Mailnix:PublicUrl", publicUrl)
        .WithServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IEmailSender>(_email));
            s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
        });

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("errorCode", out var code) ? code.GetString() : null;

    private static Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email, string? language = null) =>
        client.PostAsJsonAsync("/api/auth/forgot-password", new { email, language }, Ct);

    /// <summary>The reset link from the email, split into its parts.</summary>
    private static (Uri Link, string Email, string Token) ResetLink(EmailMessage message)
    {
        var url = message.Text.Split('\n').Single(line => line.StartsWith(PublicUrl, StringComparison.Ordinal)).Trim();
        var link = new Uri(url);
        var query = QueryHelpers.ParseQuery(link.Query);
        return (link, query["email"].ToString(), query["token"].ToString());
    }

    [Fact]
    public async Task Forgot_password_mails_a_link_that_sets_a_new_password_and_signs_out_everywhere()
    {
        (await _admin.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.PasswordResetAvailable.ShouldBeTrue();
        var phone = _app.CreateApiClient();
        (await phone.LoginAsync("user@example.com")).EnsureSuccessStatusCode();

        // A forged Host header must not end up in the link.
        var anonymous = _app.CreateApiClient();
        anonymous.DefaultRequestHeaders.Host = "evil.example";
        (await ForgotAsync(anonymous, " USER@example.com ")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var mail = await _email.WaitForAsync(m => m.To == "user@example.com", Ct);
        mail.Subject.ShouldBe("Reset your mailnix password");
        mail.Html.ShouldNotBeNull().ShouldContain("Choose a new password");
        var (link, email, token) = ResetLink(mail);
        link.GetLeftPart(UriPartial.Path).ShouldBe($"{PublicUrl}/reset-password");
        email.ShouldBe("user@example.com");

        var tooShort = await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email, token, newPassword = "short" }, Ct);
        tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email, token, newPassword = "a brand new password" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.CreateApiClient().LoginAsync("user@example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.CreateApiClient().LoginAsync("user@example.com", "a brand new password")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await phone.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var notice = await _email.WaitForAsync(m => m.Subject == "Your mailnix password was changed", Ct);
        notice.Text.ShouldContain($"{PublicUrl}/forgot-password");

        // The link works once.
        var again = await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email, token, newPassword = "yet another password" }, Ct);
        (await ErrorCodeAsync(again)).ShouldBe("ResetLinkInvalid");
    }

    [Fact]
    public async Task Unknown_addresses_get_the_same_answer_and_no_email()
    {
        (await ForgotAsync(_app.CreateApiClient(), "nobody@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ForgotAsync(_app.CreateApiClient(), "admin@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await _email.WaitForAsync(m => m.To == "admin@example.com", Ct);
        _email.Sent.ShouldNotContain(m => m.To == "nobody@example.com");
    }

    [Fact]
    public async Task Broken_or_foreign_tokens_are_refused()
    {
        var anonymous = _app.CreateApiClient();
        (await ErrorCodeAsync(await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email = "user@example.com", token = "not-a-token!", newPassword = "a brand new password" }, Ct)))
            .ShouldBe("ResetLinkInvalid");

        // A real token for one account does not work for another.
        await ForgotAsync(anonymous, "admin@example.com");
        var (_, _, token) = ResetLink(await _email.WaitForAsync(m => m.To == "admin@example.com", Ct));
        (await ErrorCodeAsync(await anonymous.PostAsJsonAsync("/api/auth/reset-password", new { email = "user@example.com", token, newPassword = "a brand new password" }, Ct)))
            .ShouldBe("ResetLinkInvalid");
    }

    [Fact]
    public async Task Emails_use_the_language_of_the_account_or_else_of_the_page()
    {
        await ForgotAsync(_app.CreateApiClient(), "user@example.com", language: "de");
        (await _email.WaitForAsync(m => m.To == "user@example.com", Ct)).Subject.ShouldBe("Dein mailnix-Passwort zurücksetzen");

        // A chosen language wins.
        (await _admin.PutAsJsonAsync("/api/profile", new { language = "en" }, Ct)).EnsureSuccessStatusCode();
        await ForgotAsync(_app.CreateApiClient(), "admin@example.com", language: "de");
        (await _email.WaitForAsync(m => m.To == "admin@example.com", Ct)).Subject.ShouldBe("Reset your mailnix password");
    }

    [Fact]
    public async Task Changing_the_password_or_two_factor_sign_in_sends_a_notice()
    {
        (await _user.PostAsJsonAsync("/api/profile/password", new { currentPassword = HttpClientAuthExtensions.DefaultPassword, newPassword = "a brand new password" }, Ct))
            .EnsureSuccessStatusCode();
        await _email.WaitForAsync(m => m.To == "user@example.com" && m.Subject == "Your mailnix password was changed", Ct);

        var setup = (await (await _user.PostAsync("/api/profile/two-factor/setup", null, Ct)).Content.ReadFromJsonAsync<AuthenticatorSetup>(Ct))!;
        var code = ProfileTests.Totp.Format(ProfileTests.Totp.Now(setup.SharedKey.Replace(" ", "", StringComparison.Ordinal)));
        (await _user.PostAsJsonAsync("/api/profile/two-factor/enable", new { code }, Ct)).EnsureSuccessStatusCode();
        var on = await _email.WaitForAsync(m => m.Subject == "Two-factor sign-in is on", Ct);
        on.Text.ShouldContain("(UTC)", customMessage: "times are given in the account's time zone");

        (await _user.PostAsJsonAsync("/api/profile/two-factor/disable", new { password = "a brand new password" }, Ct)).EnsureSuccessStatusCode();
        await _email.WaitForAsync(m => m.Subject == "Two-factor sign-in is off", Ct);
    }

    [Fact]
    public async Task Invitations_can_be_sent_by_email()
    {
        (await _admin.GetFromJsonAsync<TeamResponse>("/api/team", Ct))!.EmailAvailable.ShouldBeTrue();
        (await _admin.PutAsJsonAsync("/api/team", new { name = "Acme" }, Ct)).EnsureSuccessStatusCode();

        var created = (await (await _admin.PostAsJsonAsync("/api/team/invitations", new { email = "carol@example.com", sendEmail = true }, Ct)).Content.ReadFromJsonAsync<CreatedInvitation>(Ct))!;
        created.EmailSent.ShouldBeTrue();
        var mail = await _email.WaitForAsync(m => m.To == "carol@example.com", Ct);
        mail.Subject.ShouldBe("Join Acme on mailnix");
        mail.Text.ShouldContain(created.Url);
        created.Url.ShouldStartWith($"{PublicUrl}/register?invite=");

        // Without the box ticked, only the link is shown.
        var quiet = (await (await _admin.PostAsJsonAsync("/api/team/invitations", new { email = "dave@example.com" }, Ct)).Content.ReadFromJsonAsync<CreatedInvitation>(Ct))!;
        quiet.EmailSent.ShouldBeFalse();
    }

    [Fact]
    public async Task Invitation_emails_are_limited_per_organization_and_day()
    {
        await using var app = Create(publicUrl: PublicUrl).WithSetting("Mailnix:Email:InvitationsPerDay", "2");
        var admin = app.CreateApiClient();
        (await admin.RegisterAsync("admin@example.com")).EnsureSuccessStatusCode();
        Task<HttpResponseMessage> InviteAsync(string? email, bool sendEmail) =>
            admin.PostAsJsonAsync("/api/team/invitations", new { email, sendEmail }, Ct);

        (await InviteAsync("one@example.com", true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await InviteAsync("two@example.com", true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var refused = await InviteAsync("three@example.com", true);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("InvitationEmailLimit");
        (await InviteAsync(null, false)).StatusCode.ShouldBe(HttpStatusCode.OK, "a link to pass on yourself sends no email");
    }

    [Fact]
    public async Task The_operator_can_send_a_test_email_and_sees_why_it_fails()
    {
        (await _admin.PostAsync("/api/system/test-email", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _email.WaitForAsync(m => m.To == "admin@example.com", Ct)).Subject.ShouldBe("Test email from mailnix");
        (await _user.PostAsync("/api/system/test-email", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        _email.Failure = new EmailException("Sending through smtp.example.org failed: 535 Authentication failed");
        var failed = await _admin.PostAsync("/api/system/test-email", null, Ct);
        failed.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("errorCode").GetString().ShouldBe("EmailFailed");
        problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("535");
    }

    [Fact]
    public async Task Without_smtp_or_a_public_address_nothing_is_sent()
    {
        await using var noLinks = Create(publicUrl: null);
        var client = noLinks.CreateApiClient();
        (await client.RegisterAsync("solo@example.com")).EnsureSuccessStatusCode();
        (await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.PasswordResetAvailable.ShouldBeFalse("links need the public address");
        (await ForgotAsync(noLinks.CreateApiClient(), "solo@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        _email.IsConfigured = false;
        (await _admin.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct))!.PasswordResetAvailable.ShouldBeFalse();
        (await _admin.PostAsync("/api/system/test-email", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _admin.GetFromJsonAsync<TeamResponse>("/api/team", Ct))!.EmailAvailable.ShouldBeFalse();

        await Task.Delay(300, Ct);
        _email.Sent.ShouldBeEmpty();
    }
}
