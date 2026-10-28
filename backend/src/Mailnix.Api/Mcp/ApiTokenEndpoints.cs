using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Mcp;

/// <param name="ExpiresAt">Personal tokens: when the token stops working. Connections: when the app must sign in again.</param>
public sealed record ApiTokenResponse(
    Guid Id,
    ApiTokenKind Kind,
    string Name,
    ApiScope Scope,
    string? TokenHint,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    bool Expired);

/// <param name="McpUrl">The address MCP clients connect to.</param>
public sealed record ApiTokenListResponse(string McpUrl, IReadOnlyList<ApiTokenResponse> Tokens);

/// <param name="ExpiresInDays">Null: the token does not expire.</param>
public sealed record CreateApiTokenRequest([Required, MaxLength(100)] string Name, ApiScope Scope = ApiScope.Read, [Range(1, 365)] int? ExpiresInDays = 90);

/// <param name="Secret">The token itself. Shown only now; mailnix keeps only its hash.</param>
public sealed record CreatedApiTokenResponse(ApiTokenResponse Token, string Secret, string McpUrl);

/// <summary>Personal access tokens for MCP clients, and the apps connected through OAuth (Settings → AI assistants).</summary>
internal static class ApiTokenEndpoints
{
    public const string PersonalTokenPrefix = "mnx_pat_";
    public const int MaxTokensPerUser = 50;

    public static IEndpointRouteBuilder MapApiTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/tokens").WithTags("Tokens");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapDelete("/{id:guid}", Delete);
        return routes;
    }

    /// <summary>Ends every token and connection of the user: after a password change or "sign out other devices".</summary>
    public static Task<int> RevokeAllAsync(MailnixDbContext db, Guid userId, CancellationToken ct) =>
        db.ApiTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);

    private static async Task<Ok<ApiTokenListResponse>> List(HttpContext context, ClaimsPrincipal user, MailnixDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = time.GetUtcNow();
        var tokens = await db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct);
        return TypedResults.Ok(new ApiTokenListResponse(
            OAuthServerEndpoints.Resource(context),
            [.. tokens.OrderByDescending(t => t.CreatedAt).Select(t => ToResponse(t, now))]));
    }

    private static async Task<Results<Ok<CreatedApiTokenResponse>, ProblemHttpResult>> Create(
        CreateApiTokenRequest request,
        HttpContext context,
        ClaimsPrincipal user,
        MailnixDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (await db.ApiTokens.CountAsync(t => t.UserId == userId, ct) >= MaxTokensPerUser)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: $"At most {MaxTokensPerUser} tokens and connections.",
                extensions: new Dictionary<string, object?> { ["errorCode"] = "TooManyTokens" });
        }

        var now = time.GetUtcNow();
        var secret = SecretTokens.Create(PersonalTokenPrefix);
        var token = new ApiToken
        {
            Id = Guid.NewGuid(),
            TenantId = user.GetTenantId(db),
            UserId = userId,
            Kind = ApiTokenKind.Personal,
            Name = request.Name.Trim(),
            Scope = request.Scope,
            TokenHash = SecretTokens.Hash(secret),
            TokenHint = secret[..(PersonalTokenPrefix.Length + 4)],
            ExpiresAt = request.ExpiresInDays is { } days ? now.AddDays(days) : null,
            CreatedAt = now,
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new CreatedApiTokenResponse(ToResponse(token, now), secret, OAuthServerEndpoints.Resource(context)));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, ClaimsPrincipal user, MailnixDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        return await db.ApiTokens.Where(t => t.Id == id && t.UserId == userId).ExecuteDeleteAsync(ct) == 1
            ? TypedResults.NoContent()
            : TypedResults.NotFound();
    }

    private static ApiTokenResponse ToResponse(ApiToken t, DateTimeOffset now)
    {
        var expiresAt = t.Kind == ApiTokenKind.Personal ? t.ExpiresAt : t.RefreshExpiresAt;
        return new ApiTokenResponse(t.Id, t.Kind, t.Name, t.Scope, t.TokenHint, t.CreatedAt, t.LastUsedAt, expiresAt, expiresAt <= now);
    }
}
