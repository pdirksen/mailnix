using System.Net;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Integration.Tests.Fakes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Mailnix.Integration.Tests;

public sealed class OAuthClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OAuthClient Client(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new OAuthOptions
        {
            Microsoft = new MicrosoftOAuthOptions { ClientId = "ms-client", ClientSecret = "test-secret", Tenant = "organizations" },
        }),
        TimeProvider.System);

    [Fact]
    public async Task Microsoft_uses_its_tenant_endpoints_imap_scope_and_preferred_username()
    {
        var provider = new FakeOAuthProvider { EmailClaim = "preferred_username", Email = "user@contoso.com", RotatedRefreshToken = "refresh-2" };
        var client = Client(provider);
        var verifier = OAuthClient.CreateCodeVerifier();

        var url = new Uri(client.AuthorizationUrl(MailProvider.Microsoft, "https://mailnix.example/oauth/callback", "state-1", verifier));
        var query = QueryHelpers.ParseQuery(url.Query);
        url.GetLeftPart(UriPartial.Path).ShouldBe("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize");
        query["scope"].ToString().ShouldContain("https://outlook.office.com/IMAP.AccessAsUser.All");
        query["scope"].ToString().ShouldContain("offline_access");
        query["prompt"].ToString().ShouldBe("select_account");

        var tokens = await client.ExchangeCodeAsync(MailProvider.Microsoft, provider.Authorize(url.ToString()), verifier, "https://mailnix.example/oauth/callback", Ct);
        tokens.Email.ShouldBe("user@contoso.com");
        tokens.RefreshToken.ShouldBe("refresh-1");
        client.Get(MailProvider.Microsoft).ImapHost.ShouldBe("outlook.office365.com");

        var refreshed = await client.RefreshAsync(MailProvider.Microsoft, "refresh-1", Ct);
        refreshed.RefreshToken.ShouldBe("refresh-2");
        provider.Requests.Last()["scope"].ShouldContain("IMAP.AccessAsUser.All");
    }

    [Fact]
    public async Task Provider_errors_become_oauth_exceptions()
    {
        var provider = new FakeOAuthProvider { Revoked = true };

        var revoked = await Should.ThrowAsync<OAuthException>(() => Client(provider).RefreshAsync(MailProvider.Microsoft, "refresh-1", Ct));
        revoked.ReauthorizationRequired.ShouldBeTrue();

        var html = await Should.ThrowAsync<OAuthException>(() => Client(new StaticResponse(HttpStatusCode.BadGateway, "<html>")).RefreshAsync(MailProvider.Microsoft, "x", Ct));
        html.ReauthorizationRequired.ShouldBeFalse();
        html.Message.ShouldContain("502");
    }

    [Fact]
    public void Unconfigured_providers_cannot_be_used()
    {
        var client = Client(new FakeOAuthProvider());

        client.IsConfigured(MailProvider.Google).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => client.Get(MailProvider.Google));
    }

    private sealed class StaticResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
