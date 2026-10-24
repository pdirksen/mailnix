using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Mail;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Accounts;

public sealed record OAuthProviderInfo(MailProvider Provider, bool Configured);

/// <param name="Name">Name of the new account (default: the mailbox address).</param>
/// <param name="AccountId">Reconnect this existing account instead of adding one.</param>
public sealed record StartOAuthRequest([MaxLength(200)] string? Name = null, Guid? AccountId = null);

public sealed record StartOAuthResponse(string AuthorizationUrl);

/// <summary>What the provider sent back to /oauth/callback.</summary>
public sealed record CompleteOAuthRequest([Required, MaxLength(8000)] string State, [Required, MaxLength(8000)] string Code);

public sealed record CompleteOAuthResponse(AccountResponse Account, bool Created);

/// <summary>
/// Adding (or reconnecting) Google and Microsoft 365 mailboxes with OAuth. The provider sends the browser back to
/// the web app (/oauth/callback), which completes the sign-in here with a normal same-site request: the login cookie
/// is SameSite=Strict and would not be sent on the provider's cross-site redirect itself.
/// </summary>
internal static class OAuthEndpoints
{
    public const string CallbackPath = "/oauth/callback";

    public static RouteGroupBuilder MapOAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/oauth").WithTags("OAuth");
        group.MapGet("/providers", Providers);
        group.MapPost("/{provider}/start", Start);
        group.MapPost("/complete", Complete).RequireRateLimiting(RateLimiting.MailConnections);
        return group;
    }

    private static Ok<List<OAuthProviderInfo>> Providers(OAuthClient oauth) =>
        TypedResults.Ok(new List<OAuthProviderInfo>
        {
            new(MailProvider.Google, oauth.IsConfigured(MailProvider.Google)),
            new(MailProvider.Microsoft, oauth.IsConfigured(MailProvider.Microsoft)),
        });

    private static async Task<Results<Ok<StartOAuthResponse>, NotFound, ProblemHttpResult>> Start(
        string provider,
        StartOAuthRequest request,
        HttpRequest http,
        ClaimsPrincipal user,
        MailnixDbContext db,
        OAuthClient oauth,
        OAuthStateProtector states,
        IOptions<OAuthOptions> options,
        CancellationToken ct)
    {
        if (!Enum.TryParse<MailProvider>(provider, ignoreCase: true, out var mailProvider) || mailProvider == MailProvider.Imap || !oauth.IsConfigured(mailProvider))
        {
            return Problem(StatusCodes.Status400BadRequest, "OAuthNotConfigured", $"Signing in with {provider} is not set up on this server.");
        }

        var userId = user.GetUserId();
        string? loginHint = null;
        if (request.AccountId is { } accountId)
        {
            var account = await db.MailAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.UserId == userId, ct);
            if (account is null)
            {
                return TypedResults.NotFound();
            }

            if (account.Provider != mailProvider)
            {
                return Problem(StatusCodes.Status400BadRequest, "OAuthWrongProvider", $"This account does not use {mailProvider}.");
            }

            loginHint = account.Username;
        }

        var baseUrl = options.Value.RedirectBaseUrl?.TrimEnd('/') ?? $"{http.Scheme}://{http.Host}";
        var redirectUri = baseUrl + CallbackPath;
        var verifier = OAuthClient.CreateCodeVerifier();
        var state = states.Protect(new OAuthState(userId, mailProvider, verifier, redirectUri, request.AccountId, request.Name?.Trim()));
        return TypedResults.Ok(new StartOAuthResponse(oauth.AuthorizationUrl(mailProvider, redirectUri, state, verifier, loginHint)));
    }

    private static async Task<Results<Ok<CompleteOAuthResponse>, NotFound, ProblemHttpResult>> Complete(
        CompleteOAuthRequest request,
        ClaimsPrincipal user,
        MailnixDbContext db,
        OAuthClient oauth,
        OAuthStateProtector states,
        IMailboxProbe probe,
        MailSecretProtector secrets,
        AccountCredentials credentials,
        IWorkerSignals signals,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (states.Unprotect(request.State) is not { } state || state.UserId != userId)
        {
            return Problem(StatusCodes.Status400BadRequest, "OAuthStateInvalid", "The sign-in took too long or was started elsewhere. Please start it again.");
        }

        OAuthTokens tokens;
        try
        {
            tokens = await oauth.ExchangeCodeAsync(state.Provider, request.Code, state.CodeVerifier, state.RedirectUri, ct);
        }
        catch (OAuthException ex)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "OAuthFailed", $"{state.Provider} did not complete the sign-in ({ex.Message}).");
        }
        catch (HttpRequestException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "OAuthFailed", $"{state.Provider} could not be reached. Please try again.");
        }

        if (tokens.RefreshToken is not { Length: > 0 } refreshToken)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "OAuthNoRefreshToken",
                $"{state.Provider} did not allow lasting access. Remove mailnix from the apps with access to your {state.Provider} account and try again.");
        }

        if (tokens.Email is not { } email)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "OAuthNoEmail", $"{state.Provider} did not tell which mailbox you signed in to.");
        }

        var provider = oauth.Get(state.Provider);
        var settings = new ImapConnectionSettings(provider.ImapHost, provider.ImapPort, provider.ImapSecurity, email, tokens.AccessToken, ImapAuthentication.OAuth2);
        MailboxProbeResult result;
        try
        {
            result = await probe.ProbeAsync(settings, includeCounts: false, ct);
        }
        catch (MailConnectionException ex)
        {
            return AccountEndpoints.ConnectionProblem(ex);
        }

        var now = time.GetUtcNow();
        MailAccount account;
        bool created;
        if (state.AccountId is { } accountId)
        {
            if (await db.MailAccounts.SingleOrDefaultAsync(a => a.Id == accountId && a.UserId == userId, ct) is not { } existing)
            {
                return TypedResults.NotFound();
            }

            account = existing;

            if (account.Provider != state.Provider || !account.Username.Equals(email, StringComparison.OrdinalIgnoreCase))
            {
                return Problem(StatusCodes.Status409Conflict, "OAuthDifferentMailbox", $"You signed in as {email}, but this account is {account.Username}.");
            }

            account.EncryptedSecret = secrets.Protect(refreshToken);
            created = false;
        }
        else
        {
            var special = result.SpecialFolders;
            if (state.Provider == MailProvider.Google && special.Archive is null)
            {
                // Gmail archives by removing the Inbox label; the message stays in "All Mail".
                special = special with { Archive = result.Folders.FirstOrDefault(f => f.SpecialUse == SpecialFolderKind.All)?.Path };
            }

            account = new MailAccount
            {
                Id = Guid.CreateVersion7(),
                TenantId = await db.Users.Where(u => u.Id == userId).Select(u => u.TenantId).SingleAsync(ct),
                UserId = userId,
                Name = string.IsNullOrWhiteSpace(state.Name) ? email : state.Name,
                Provider = state.Provider,
                Host = provider.ImapHost,
                Port = provider.ImapPort,
                Security = provider.ImapSecurity,
                Username = email,
                EncryptedSecret = secrets.Protect(refreshToken),
                WatchedFolders = ["INBOX"],
                SpecialFolders = AccountEndpoints.ToEntity(special),
                CreatedAt = now,
            };
            db.MailAccounts.Add(account);
            created = true;
        }

        AccountEndpoints.ApplySuccess(account, result, now);
        await db.SaveChangesAsync(ct);
        credentials.Remember(account.Id, refreshToken, tokens);
        signals.AccountsChanged();
        return TypedResults.Ok(new CompleteOAuthResponse(AccountResponse.From(account), created));
    }

    private static ProblemHttpResult Problem(int status, string errorCode, string title) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}
