using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Mailnix.Api.Email;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Mcp;

/// <summary>
/// A small OAuth 2.1 authorization server for MCP clients (e.g. Claude.ai connectors): metadata (RFC 8414), dynamic
/// client registration (RFC 7591, public clients only), authorization codes with PKCE S256, refresh token rotation
/// and revocation (RFC 7009). The user signs in and agrees on the SPA page /connect (see <see cref="ConnectEndpoints"/>).
/// </summary>
internal static class OAuthServerEndpoints
{
    public const string ReadScope = "mailnix.read";
    public const string WriteScope = "mailnix.write";
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(60);
    public const string AccessTokenPrefix = "mnx_at_";
    public const string RefreshTokenPrefix = "mnx_rt_";

    private const int MaxRedirectUris = 10;

    /// <summary>Registered apps that never connected are removed after this (registration needs no sign-in).</summary>
    private static readonly TimeSpan UnusedAppLifetime = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapOAuthServerEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/.well-known/oauth-authorization-server", Metadata).ExcludeFromDescription().AllowAnonymous();
        var oauth = routes.MapGroup("/oauth2").ExcludeFromDescription().AllowAnonymous();
        oauth.MapPost("/register", Register).RequireRateLimiting(RateLimiting.Auth);
        oauth.MapGet("/authorize", Authorize);
        oauth.MapPost("/token", Token).RequireRateLimiting(RateLimiting.Auth);
        oauth.MapPost("/revoke", Revoke).RequireRateLimiting(RateLimiting.Auth);
        return routes;
    }

    /// <summary>Mailnix:PublicUrl (or OAuth:RedirectBaseUrl), else the address the request came to.</summary>
    public static string Issuer(HttpContext context) =>
        context.RequestServices.GetRequiredService<PublicLinks>().BaseUrl
        ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

    /// <summary>The MCP endpoint: the resource tokens are issued for (RFC 8707).</summary>
    public static string Resource(HttpContext context) => Issuer(context) + McpSetup.Path;

    public static string ScopeString(ApiScope scope) => scope == ApiScope.ReadWrite ? $"{ReadScope} {WriteScope}" : ReadScope;

    private static JsonHttpResult<AuthorizationServerMetadata> Metadata(HttpContext context)
    {
        var issuer = Issuer(context);
        return TypedResults.Json(new AuthorizationServerMetadata(
            issuer,
            $"{issuer}/oauth2/authorize",
            $"{issuer}/oauth2/token",
            $"{issuer}/oauth2/register",
            $"{issuer}/oauth2/revoke",
            ["code"],
            ["authorization_code", "refresh_token"],
            ["S256"],
            ["none"],
            [ReadScope, WriteScope]));
    }

    private static async Task<IResult> Register(ClientRegistrationRequest request, MailnixDbContext db, TimeProvider time, CancellationToken ct)
    {
        var redirectUris = request.RedirectUris?.Select(u => u?.Trim() ?? "").Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (redirectUris.Count is 0 or > MaxRedirectUris || redirectUris.Any(u => !IsAllowedRedirectUri(u)))
        {
            return OAuthError("invalid_redirect_uri", "Redirect URIs must be https addresses, loopback addresses or app schemes.");
        }

        if (request.TokenEndpointAuthMethod is { } method && method != "none")
        {
            return OAuthError("invalid_client_metadata", "Only public clients (token_endpoint_auth_method \"none\") are supported.");
        }

        if (request.GrantTypes is { } grants && grants.Any(g => g is not ("authorization_code" or "refresh_token")))
        {
            return OAuthError("invalid_client_metadata", "Only the grant types authorization_code and refresh_token are supported.");
        }

        var now = time.GetUtcNow();
        var unusedBefore = now - UnusedAppLifetime;
        await db.ConnectedApps
            .Where(a => a.CreatedAt < unusedBefore && !db.ApiTokens.Any(t => t.ClientId == a.Id))
            .ExecuteDeleteAsync(ct);

        var name = request.ClientName?.Trim() is { Length: > 0 } given ? given[..Math.Min(given.Length, 200)] : new Uri(redirectUris[0]).Host;
        var app = new ConnectedApp { Id = Guid.NewGuid(), Name = name, RedirectUris = redirectUris, CreatedAt = now };
        db.ConnectedApps.Add(app);
        await db.SaveChangesAsync(ct);

        return TypedResults.Json(
            new ClientRegistrationResponse(
                app.Id.ToString("N"),
                app.CreatedAt.ToUnixTimeSeconds(),
                app.Name,
                app.RedirectUris,
                ["authorization_code", "refresh_token"],
                ["code"],
                "none"),
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// https everywhere, http only to the own computer (desktop apps listening on a loopback port, RFC 8252), and
    /// private-use schemes of apps in reverse domain notation (e.g. com.example.app:/callback).
    /// </summary>
    internal static bool IsAllowedRedirectUri(string uri)
    {
        if (uri.Length > 2000 || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0)
        {
            return false;
        }

        return parsed.Scheme switch
        {
            "https" => true,
            "http" => parsed.IsLoopback,
            _ => parsed.Scheme.Contains('.', StringComparison.Ordinal) && parsed.Scheme is not ("javascript" or "data" or "file"),
        };
    }

    /// <summary>The browser goes on to the SPA, which signs the user in and asks for consent.</summary>
    private static RedirectHttpResult Authorize(HttpContext context) => TypedResults.Redirect($"/connect{context.Request.QueryString}");

    private static async Task<IResult> Token(HttpContext context, MailnixDbContext db, TimeProvider time, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasFormContentType)
        {
            return OAuthError("invalid_request", "Expected a form-encoded body.");
        }

        var form = await context.Request.ReadFormAsync(ct);
        string? Field(string name) => form[name].ToString() is { Length: > 0 } value ? value : null;
        if (!Guid.TryParseExact(Field("client_id"), "N", out var clientId))
        {
            return OAuthError("invalid_client", "Unknown client.");
        }

        var now = time.GetUtcNow();
        return Field("grant_type") switch
        {
            "authorization_code" => await ExchangeCodeAsync(context, db, clientId, Field("code"), Field("redirect_uri"), Field("code_verifier"), Field("resource"), now, ct),
            "refresh_token" => await RefreshAsync(db, clientId, Field("refresh_token"), Field("scope"), now, ct),
            _ => OAuthError("unsupported_grant_type", "Supported: authorization_code, refresh_token."),
        };
    }

    private static async Task<IResult> ExchangeCodeAsync(
        HttpContext context,
        MailnixDbContext db,
        Guid clientId,
        string? code,
        string? redirectUri,
        string? verifier,
        string? resource,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (code is null || redirectUri is null || verifier is null)
        {
            return OAuthError("invalid_request", "code, redirect_uri and code_verifier are required.");
        }

        var hash = SecretTokens.Hash(code);
        var grant = await db.AuthorizationCodes.AsNoTracking().SingleOrDefaultAsync(c => c.CodeHash == hash, ct);

        // Single use: whoever deletes the code first gets the tokens.
        if (grant is null || await db.AuthorizationCodes.Where(c => c.Id == grant.Id).ExecuteDeleteAsync(ct) != 1 || grant.ExpiresAt <= now)
        {
            return OAuthError("invalid_grant", "The authorization code is invalid, expired or already used.");
        }

        if (grant.ClientId != clientId || grant.RedirectUri != redirectUri || !PkceMatches(verifier, grant.CodeChallenge))
        {
            return OAuthError("invalid_grant", "The authorization code does not match this request.");
        }

        if (resource is not null && resource.TrimEnd('/') != (grant.Resource ?? Resource(context)))
        {
            return OAuthError("invalid_target", "Tokens are only issued for this server's MCP endpoint.");
        }

        if (await db.ConnectedApps.AsNoTracking().SingleOrDefaultAsync(a => a.Id == clientId, ct) is not { } app)
        {
            return OAuthError("invalid_client", "Unknown client.");
        }

        // One connection per app and user: connecting again replaces the earlier one.
        await db.ApiTokens.Where(t => t.UserId == grant.UserId && t.ClientId == clientId).ExecuteDeleteAsync(ct);

        var access = SecretTokens.Create(AccessTokenPrefix);
        var refresh = SecretTokens.Create(RefreshTokenPrefix);
        db.ApiTokens.Add(new ApiToken
        {
            Id = Guid.NewGuid(),
            TenantId = grant.TenantId,
            UserId = grant.UserId,
            Kind = ApiTokenKind.Connection,
            Name = app.Name,
            Scope = grant.Scope,
            ClientId = clientId,
            TokenHash = SecretTokens.Hash(access),
            ExpiresAt = now + AccessTokenLifetime,
            RefreshTokenHash = SecretTokens.Hash(refresh),
            RefreshExpiresAt = now + RefreshTokenLifetime,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return TokenResponse(access, refresh, grant.Scope);
    }

    private static async Task<IResult> RefreshAsync(MailnixDbContext db, Guid clientId, string? refreshToken, string? scope, DateTimeOffset now, CancellationToken ct)
    {
        if (refreshToken is null)
        {
            return OAuthError("invalid_request", "refresh_token is required.");
        }

        var hash = SecretTokens.Hash(refreshToken);
        var connection = await db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.RefreshTokenHash == hash, ct);
        if (connection is null || connection.ClientId != clientId || connection.RefreshExpiresAt <= now)
        {
            return OAuthError("invalid_grant", "The refresh token is invalid or has expired.");
        }

        // A refresh may narrow the scope, never widen it.
        var newScope = connection.Scope;
        if (scope is not null)
        {
            var requested = ParseScope(scope);
            if (requested is null || (requested == ApiScope.ReadWrite && connection.Scope != ApiScope.ReadWrite))
            {
                return OAuthError("invalid_scope", "The requested scope exceeds what was granted.");
            }

            newScope = requested.Value;
        }

        var access = SecretTokens.Create(AccessTokenPrefix);
        var refresh = SecretTokens.Create(RefreshTokenPrefix);
        var accessHash = SecretTokens.Hash(access);
        var refreshHash = SecretTokens.Hash(refresh);

        // Rotation: the old refresh token works exactly once, even when two requests race.
        var updated = await db.ApiTokens
            .Where(t => t.Id == connection.Id && t.RefreshTokenHash == hash)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.TokenHash, accessHash)
                    .SetProperty(t => t.ExpiresAt, now + AccessTokenLifetime)
                    .SetProperty(t => t.RefreshTokenHash, refreshHash)
                    .SetProperty(t => t.RefreshExpiresAt, now + RefreshTokenLifetime)
                    .SetProperty(t => t.Scope, newScope),
                ct);
        return updated == 1 ? TokenResponse(access, refresh, newScope) : OAuthError("invalid_grant", "The refresh token is invalid or has expired.");
    }

    /// <summary>Revoking either token of a connection ends the whole connection. Unknown tokens are not an error.</summary>
    private static async Task<IResult> Revoke(HttpContext context, MailnixDbContext db, CancellationToken ct)
    {
        if (!context.Request.HasFormContentType)
        {
            return OAuthError("invalid_request", "Expected a form-encoded body.");
        }

        var form = await context.Request.ReadFormAsync(ct);
        if (form["token"].ToString() is { Length: > 0 } token)
        {
            var hash = SecretTokens.Hash(token);
            await db.ApiTokens
                .Where(t => t.Kind == ApiTokenKind.Connection && (t.TokenHash == hash || t.RefreshTokenHash == hash))
                .ExecuteDeleteAsync(ct);
        }

        return TypedResults.Ok();
    }

    /// <summary>"mailnix.read", "mailnix.write" or both; null when the string names none of them.</summary>
    public static ApiScope? ParseScope(string? scope)
    {
        var parts = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Contains(WriteScope) ? ApiScope.ReadWrite : parts.Contains(ReadScope) ? ApiScope.Read : null;
    }

    internal static bool PkceMatches(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128)
        {
            return false;
        }

        var computed = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static JsonHttpResult<TokenResult> TokenResponse(string access, string refresh, ApiScope scope) =>
        TypedResults.Json(new TokenResult(access, "Bearer", (int)AccessTokenLifetime.TotalSeconds, refresh, ScopeString(scope)));

    private static JsonHttpResult<OAuthErrorResult> OAuthError(string error, string description) =>
        TypedResults.Json(new OAuthErrorResult(error, description), statusCode: StatusCodes.Status400BadRequest);

    private sealed record AuthorizationServerMetadata(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
        [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
        [property: JsonPropertyName("revocation_endpoint")] string RevocationEndpoint,
        [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
        [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
        [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
        [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
        [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported);

    internal sealed record ClientRegistrationRequest(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string?>? RedirectUris,
        [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

    private sealed record ClientRegistrationResponse(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
        [property: JsonPropertyName("client_name")] string ClientName,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
        [property: JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
        [property: JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod);

    private sealed record TokenResult(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("scope")] string Scope);

    private sealed record OAuthErrorResult(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription);
}
