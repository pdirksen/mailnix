using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Mcp;

/// <param name="RedirectHost">Where the browser goes back to after the decision, to show the user.</param>
public sealed record ConnectAppResponse(string Name, string RedirectHost, ApiScope RequestedScope);

/// <summary>The parameters of the authorization request (passed through from /oauth2/authorize) and the decision.</summary>
/// <param name="AllowChanges">Grant <see cref="ApiScope.ReadWrite"/> instead of read-only access.</param>
public sealed record ConnectRequest(
    [Required, MaxLength(100)] string ClientId,
    [Required, MaxLength(2000)] string RedirectUri,
    [MaxLength(50)] string? ResponseType,
    [MaxLength(200)] string? CodeChallenge,
    [MaxLength(20)] string? CodeChallengeMethod,
    [MaxLength(2000)] string? State,
    [MaxLength(2000)] string? Resource,
    bool Approve,
    bool AllowChanges);

/// <param name="RedirectUrl">Where the SPA sends the browser: the app's redirect address with the code or an error.</param>
public sealed record ConnectResponse(string RedirectUrl);

/// <summary>The consent step of the OAuth flow, called by the SPA page /connect with the user's cookie.</summary>
internal static class ConnectEndpoints
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/connect").WithTags("Tokens");
        group.MapGet("/app", App);
        group.MapPost("/", Decide);
        return routes;
    }

    private static async Task<Results<Ok<ConnectAppResponse>, ProblemHttpResult>> App(
        string clientId,
        string redirectUri,
        MailnixDbContext db,
        CancellationToken ct,
        string? scope = null)
    {
        if (await FindAppAsync(db, clientId, redirectUri, ct) is not { } app)
        {
            return InvalidApp();
        }

        return TypedResults.Ok(new ConnectAppResponse(app.Name, new Uri(redirectUri).Host, OAuthServerEndpoints.ParseScope(scope) ?? ApiScope.Read));
    }

    private static async Task<Results<Ok<ConnectResponse>, ProblemHttpResult>> Decide(
        ConnectRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        // Without a known app and one of its registered addresses, there is nowhere safe to send an answer to.
        if (await FindAppAsync(db, request.ClientId, request.RedirectUri, ct) is not { } app)
        {
            return InvalidApp();
        }

        var issuer = OAuthServerEndpoints.Issuer(context);
        string Answer(IReadOnlyDictionary<string, string?> parameters)
        {
            var query = parameters.Append(new("state", request.State)).Append(new("iss", issuer))
                .Where(p => p.Value is not null)
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}");
            var separator = request.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            return $"{request.RedirectUri}{separator}{string.Join('&', query)}";
        }

        Ok<ConnectResponse> Error(string error, string description) =>
            TypedResults.Ok(new ConnectResponse(Answer(new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description })));

        if (request.ResponseType != "code")
        {
            return Error("unsupported_response_type", "Only response_type=code is supported.");
        }

        if (request.CodeChallenge is not { Length: >= 43 } || request.CodeChallengeMethod != "S256")
        {
            return Error("invalid_request", "PKCE with code_challenge_method=S256 is required.");
        }

        var resource = request.Resource?.TrimEnd('/');
        if (resource is not null && resource != OAuthServerEndpoints.Resource(context))
        {
            return Error("invalid_target", "This server only issues tokens for its own MCP endpoint.");
        }

        if (!request.Approve)
        {
            return Error("access_denied", "The user declined.");
        }

        var now = time.GetUtcNow();
        await db.AuthorizationCodes.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(ct);

        var code = SecretTokens.Create();
        db.AuthorizationCodes.Add(new AuthorizationCode
        {
            Id = Guid.NewGuid(),
            CodeHash = SecretTokens.Hash(code),
            ClientId = app.Id,
            TenantId = user.GetTenantId(db),
            UserId = user.GetUserId(),
            RedirectUri = request.RedirectUri,
            CodeChallenge = request.CodeChallenge,
            Scope = request.AllowChanges ? ApiScope.ReadWrite : ApiScope.Read,
            Resource = resource,
            ExpiresAt = now + CodeLifetime,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new ConnectResponse(Answer(new Dictionary<string, string?> { ["code"] = code })));
    }

    private static async Task<ConnectedApp?> FindAppAsync(MailnixDbContext db, string clientId, string redirectUri, CancellationToken ct) =>
        Guid.TryParseExact(clientId, "N", out var id) && await db.ConnectedApps.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } app
            && app.RedirectUris.Contains(redirectUri)
            ? app
            : null;

    private static ProblemHttpResult InvalidApp() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Unknown app or redirect address.",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "ConnectInvalidApp" });
}
