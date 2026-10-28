using System.Security.Claims;
using System.Text.Encodings.Web;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Mcp;

internal static class McpClaims
{
    /// <summary>The token's scope (<see cref="ApiScope"/>); only bearer tokens carry it, never the cookie.</summary>
    public const string Scope = "mailnix:scope";

    /// <summary>The <see cref="ApiToken"/> id, for per-token rate limits.</summary>
    public const string Token = "mailnix:token";

    /// <summary>The token's or app's name, recorded as the origin of rules and runs it changes.</summary>
    public const string Origin = "mailnix:origin";
}

internal static class McpPolicies
{
    /// <summary>Any valid MCP token.</summary>
    public const string Read = "McpRead";

    /// <summary>A token that may draft rules, start runs and undo.</summary>
    public const string Write = "McpWrite";
}

/// <summary>
/// Authenticates the MCP endpoint with bearer tokens (personal tokens and OAuth access tokens). The token is looked
/// up by its hash; it carries the user, the tenant and the scope.
/// </summary>
internal sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    MailnixDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpToken";

    /// <summary>"Last used" is written at most this often, not on every request.</summary>
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length is 0 or > 200)
        {
            return AuthenticateResult.Fail("Invalid token.");
        }

        var hash = SecretTokens.Hash(token);
        var now = time.GetUtcNow();
        var found = await db.ApiTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.UserId, t.TenantId, t.Kind, t.Name, t.Scope, t.ExpiresAt, t.LastUsedAt })
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (found is null || found.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("The token is invalid or has expired.");
        }

        if (found.LastUsedAt is not { } lastUsed || now - lastUsed >= LastUsedPrecision)
        {
            await db.ApiTokens.Where(t => t.Id == found.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, found.UserId.ToString()),
                new Claim(MailnixClaimsPrincipalFactory.TenantClaim, found.TenantId.ToString()),
                new Claim(McpClaims.Scope, found.Scope.ToString()),
                new Claim(McpClaims.Token, found.Id.ToString()),
                new Claim(McpClaims.Origin, found.Name),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
