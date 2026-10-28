using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Mailnix.Api.Auth;
using Mailnix.Api.Profile;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

public sealed class ProfileTests : IAsyncLifetime
{
    // Cookies are checked against the database on every request here, so "signed out elsewhere" shows at once.
    private readonly MailnixAppFactory _app = new MailnixAppFactory()
        .WithServices(s => s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero));

    private HttpClient _admin = null!;
    private HttpClient _user = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _admin = _app.CreateApiClient();
        (await _admin.RegisterAsync("admin@example.com")).EnsureSuccessStatusCode();
        _user = _app.CreateApiClient();
        (await _user.RegisterAsync("user@example.com")).EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static async Task<ProfileResponse> ProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileResponse>("/api/profile", Ct))!;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("errorCode", out var code) ? code.GetString() : null;

    [Fact]
    public async Task Signup_takes_time_zone_and_language_from_the_browser()
    {
        var client = _app.CreateApiClient();
        (await client.PostAsJsonAsync("/api/auth/register", new { email = "berlin@example.com", password = HttpClientAuthExtensions.DefaultPassword, timeZone = "Europe/Berlin", language = "de" }, Ct))
            .EnsureSuccessStatusCode();
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me", Ct);
        me!.TimeZone.ShouldBe("Europe/Berlin");
        me.Language.ShouldBe("de");

        var odd = _app.CreateApiClient();
        (await odd.PostAsJsonAsync("/api/auth/register", new { email = "odd@example.com", password = HttpClientAuthExtensions.DefaultPassword, timeZone = "Mars/Olympus", language = "xx" }, Ct))
            .EnsureSuccessStatusCode();
        me = await odd.GetFromJsonAsync<MeResponse>("/api/auth/me", Ct);
        me!.TimeZone.ShouldBe("UTC");
        me.Language.ShouldBeNull("not chosen: the browser's language applies");
    }

    [Fact]
    public async Task Profile_and_preferences_can_be_changed()
    {
        var profile = await ProfileAsync(_user);
        profile.Email.ShouldBe("user@example.com");
        profile.OrganizationName.ShouldBe("Default");
        profile.TwoFactor.Enabled.ShouldBeFalse();

        var response = await _user.PutAsJsonAsync("/api/profile", new { displayName = "  Pat ", timeZone = "America/New_York", language = "de" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        profile = (await response.Content.ReadFromJsonAsync<ProfileResponse>(Ct))!;
        (profile.DisplayName, profile.TimeZone, profile.Language).ShouldBe(("Pat", "America/New_York", "de"));

        // Omitted fields stay; an empty name clears it.
        profile = (await (await _user.PutAsJsonAsync("/api/profile", new { displayName = "" }, Ct)).Content.ReadFromJsonAsync<ProfileResponse>(Ct))!;
        (profile.DisplayName, profile.TimeZone, profile.Language).ShouldBe((null, "America/New_York", "de"));

        (await _user.PutAsJsonAsync("/api/profile", new { timeZone = "Nowhere/Special" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _user.PutAsJsonAsync("/api/profile", new { language = "fr" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Own_addresses_are_normalized_and_used_by_the_rules()
    {
        var accountId = await Seed.AccountAsync(_app, "user@example.com");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.Where(a => a.Id == accountId)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Username, "pat@work.example"), Ct);
        }

        var response = await _user.PutAsJsonAsync("/api/profile/addresses", new { addresses = new[] { " Pat@Alias.example ", "pat@alias.example", "PAT@work.example", "news@pat.example" } }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var profile = (await response.Content.ReadFromJsonAsync<ProfileResponse>(Ct))!;
        profile.Addresses.ShouldBe(["news@pat.example", "pat@alias.example"], "the mailbox user name counts anyway");
        profile.AutomaticAddresses.ShouldBe(["pat@work.example", "user@example.com"]);

        (await _user.PutAsJsonAsync("/api/profile/addresses", new { addresses = new[] { "Pat <pat@alias.example>" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _user.PutAsJsonAsync("/api/profile/addresses", new { addresses = new[] { "not an address" } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using var check = _app.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var account = await db.MailAccounts.SingleAsync(a => a.Id == accountId, Ct);
        var loaded = await check.ServiceProvider.GetRequiredService<RuleSetLoader>().LoadAsync(account, Ct);
        loaded.Context.MyAddresses.ShouldBe(["news@pat.example", "pat@alias.example", "pat@work.example", "user@example.com"], ignoreOrder: true);
    }

    [Fact]
    public async Task Changing_the_password_keeps_this_session_and_signs_out_the_others()
    {
        var phone = _app.CreateApiClient();
        (await phone.LoginAsync("user@example.com")).EnsureSuccessStatusCode();

        var wrong = await _user.PostAsJsonAsync("/api/profile/password", new { currentPassword = "not my password", newPassword = "a brand new password" }, Ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(wrong)).ShouldBe("WrongPassword");
        (await _user.PostAsJsonAsync("/api/profile/password", new { currentPassword = HttpClientAuthExtensions.DefaultPassword, newPassword = "short" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _user.PostAsJsonAsync("/api/profile/password", new { currentPassword = HttpClientAuthExtensions.DefaultPassword, newPassword = "a brand new password" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _user.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await phone.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.CreateApiClient().LoginAsync("user@example.com", "a brand new password")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Other_devices_can_be_signed_out()
    {
        var phone = _app.CreateApiClient();
        (await phone.LoginAsync("user@example.com")).EnsureSuccessStatusCode();

        (await _user.PostAsync("/api/profile/sign-out-others", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _user.GetAsync("/api/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await phone.GetAsync("/api/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Two_factor_sign_in_with_an_authenticator_app_and_recovery_codes()
    {
        var setup = (await (await _user.PostAsync("/api/profile/two-factor/setup", null, Ct)).Content.ReadFromJsonAsync<AuthenticatorSetup>(Ct))!;
        setup.AuthenticatorUri.ShouldStartWith("otpauth://totp/mailnix%3Auser%40example.com?secret=");
        var key = setup.SharedKey.Replace(" ", "", StringComparison.Ordinal);

        var wrong = await _user.PostAsJsonAsync("/api/profile/two-factor/enable", new { code = "000000" }, Ct);
        (await ErrorCodeAsync(wrong)).ShouldBe("InvalidCode");
        var enabled = await _user.PostAsJsonAsync("/api/profile/two-factor/enable", new { code = Totp.Format(Totp.Now(key)) }, Ct);
        enabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codes = (await enabled.Content.ReadFromJsonAsync<RecoveryCodesResponse>(Ct))!.Codes;
        codes.Count.ShouldBe(10);
        (await ProfileAsync(_user)).TwoFactor.ShouldBe(new TwoFactorStatus(true, 10));

        // The password alone is no longer enough.
        var browser = _app.CreateApiClient();
        var first = await browser.LoginAsync("user@example.com");
        first.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(first)).ShouldBe("TwoFactorRequired");
        (await browser.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(await browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = "123456", isRecoveryCode = false, rememberMe = false }, Ct)))
            .ShouldBe("InvalidCode");
        (await browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = Totp.Format(Totp.Now(key)), isRecoveryCode = false, rememberMe = false }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A recovery code works once, typed carelessly.
        var careless = codes[0].Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var laptop = _app.CreateApiClient();
        await laptop.LoginAsync("user@example.com");
        (await laptop.PostAsJsonAsync("/api/auth/login/two-factor", new { code = careless, isRecoveryCode = true, rememberMe = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = _app.CreateApiClient();
        await again.LoginAsync("user@example.com");
        (await ErrorCodeAsync(await again.PostAsJsonAsync("/api/auth/login/two-factor", new { code = codes[0], isRecoveryCode = true, rememberMe = false }, Ct))).ShouldBe("InvalidCode");
        (await ProfileAsync(_user)).TwoFactor.RecoveryCodesLeft.ShouldBe(9);

        // Without the password step there is nothing to complete.
        (await ErrorCodeAsync(await _app.CreateApiClient().PostAsJsonAsync("/api/auth/login/two-factor", new { code = "123456", isRecoveryCode = false, rememberMe = false }, Ct)))
            .ShouldBe("TwoFactorExpired");

        var renewed = await _user.PostAsJsonAsync("/api/profile/two-factor/recovery-codes", new { password = HttpClientAuthExtensions.DefaultPassword }, Ct);
        (await renewed.Content.ReadFromJsonAsync<RecoveryCodesResponse>(Ct))!.Codes.ShouldNotContain(codes[1]);

        (await ErrorCodeAsync(await _user.PostAsJsonAsync("/api/profile/two-factor/disable", new { password = "wrong password!" }, Ct))).ShouldBe("WrongPassword");
        (await _user.PostAsJsonAsync("/api/profile/two-factor/disable", new { password = HttpClientAuthExtensions.DefaultPassword }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _app.CreateApiClient().LoginAsync("user@example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ProfileAsync(_user)).TwoFactor.ShouldBe(new TwoFactorStatus(false, 0));
    }

    [Fact]
    public async Task Users_can_delete_their_account_but_the_installation_admin_cannot()
    {
        var accountId = await Seed.AccountAsync(_app, "user@example.com");

        (await ErrorCodeAsync(await _admin.PostAsJsonAsync("/api/profile/delete", new { password = HttpClientAuthExtensions.DefaultPassword }, Ct))).ShouldBe("CannotDeleteAdmin");
        (await ErrorCodeAsync(await _user.PostAsJsonAsync("/api/profile/delete", new { password = "wrong password!" }, Ct))).ShouldBe("WrongPassword");

        (await _user.PostAsJsonAsync("/api/profile/delete", new { password = HttpClientAuthExtensions.DefaultPassword }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _user.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.CreateApiClient().LoginAsync("user@example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var scope = _app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.AnyAsync(a => a.Id == accountId, Ct)).ShouldBeFalse();
    }

    /// <summary>What an authenticator app computes (RFC 6238: 30-second steps, 6 digits, HMAC-SHA1).</summary>
    internal static class Totp
    {
        public static int Now(string base32Key) => Compute(Base32(base32Key), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

        public static string Format(int code) => code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

        private static int Compute(byte[] key, long step)
        {
            var counter = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(counter);
            }

#pragma warning disable CA5350 // RFC 6238 and every authenticator app use HMAC-SHA1
            var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
            var offset = hash[^1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return binary % 1_000_000;
        }

        private static byte[] Base32(string text)
        {
            const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bits = 0;
            var value = 0;
            var bytes = new List<byte>();
            foreach (var c in text.TrimEnd('=').ToUpperInvariant())
            {
                value = (value << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)(value >> (bits - 8)));
                    bits -= 8;
                }
            }

            return [.. bytes];
        }
    }
}
