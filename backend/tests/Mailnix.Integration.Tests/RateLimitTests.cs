using System.Net;

namespace Mailnix.Integration.Tests;

public sealed class RateLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_in_attempts_are_limited_per_address()
    {
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:RateLimits:AuthPerMinute", "3");
        (await app.CreateApiClient().RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();

        // Two more attempts are allowed, then the address waits, whatever account it tries.
        (await app.CreateApiClient().LoginAsync("owner@example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.CreateApiClient().LoginAsync("owner@example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.CreateApiClient().LoginAsync("owner@example.com")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await app.CreateApiClient().RegisterAsync("new@example.com")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Api_requests_are_limited_per_user()
    {
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:RateLimits:ApiRequestsPerMinute", "5");
        var busy = app.CreateApiClient();
        (await busy.RegisterAsync("busy@example.com")).EnsureSuccessStatusCode();
        var calm = app.CreateApiClient();
        (await calm.RegisterAsync("calm@example.com")).EnsureSuccessStatusCode();

        for (var i = 0; i < 5; i++)
        {
            (await busy.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await busy.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await calm.GetAsync("/api/auth/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "other users are not affected");
    }
}
