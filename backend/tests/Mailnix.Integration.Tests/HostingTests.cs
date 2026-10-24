using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mailnix.Integration.Tests;

public sealed class HostingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_endpoint_reports_healthy_without_authentication()
    {
        await using var app = new MailnixAppFactory();

        var response = await app.CreateClient().GetAsync("/healthz", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task State_changing_api_requests_without_csrf_header_are_rejected()
    {
        await using var app = new MailnixAppFactory();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { email = "user@example.com", password = HttpClientAuthExtensions.DefaultPassword },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unknown_api_routes_return_404_instead_of_the_spa()
    {
        await using var app = new MailnixAppFactory();

        var response = await app.CreateApiClient().GetAsync("/api/does-not-exist", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        await using var app = new MailnixAppFactory();

        var response = await app.CreateClient().GetAsync("/healthz", Ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Worker_mode_exposes_only_health()
    {
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:Mode", "Worker");
        var client = app.CreateApiClient();

        (await client.GetAsync("/healthz", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/auth/config", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
