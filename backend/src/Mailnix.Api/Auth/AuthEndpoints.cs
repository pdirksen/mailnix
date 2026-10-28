using System.Security.Claims;
using System.Text;
using Mailnix.Api.Email;
using Mailnix.Api.Hosting;
using Mailnix.Api.Mcp;
using Mailnix.Api.Profile;
using Mailnix.Api.Team;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Mailnix.Infrastructure.Options;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Auth;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth").WithTags("Auth");

        group.MapGet("/config", GetConfig).AllowAnonymous();
        group.MapGet("/invitations/{token}", GetInvitation).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/login/two-factor", LoginTwoFactor).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/forgot-password", ForgotPassword).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/reset-password", ResetPassword).AllowAnonymous().RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/logout", Logout);
        group.MapGet("/me", Me);

        return group;
    }

    private static async Task<Ok<AuthConfigResponse>> GetConfig(
        UserManager<AppUser> userManager, IOptions<AuthOptions> options, IOptions<TenancyOptions> tenancy, AccountEmails emails) =>
        TypedResults.Ok(new AuthConfigResponse(
            options.Value.AllowRegistration || !await userManager.Users.AnyAsync(),
            tenancy.Value.Mode == TenancyMode.MultiTenant,
            emails.CanSendLinks));

    /// <summary>
    /// Sends a reset link when the address has an account. The answer is the same either way (and the email goes out
    /// in the background), so nobody can find out which addresses have accounts.
    /// </summary>
    private static async Task<Accepted> ForgotPassword(ForgotPasswordRequest request, UserManager<AppUser> userManager, AccountEmails emails)
    {
        if (emails.CanSendLinks && await userManager.FindByEmailAsync(request.Email.Trim()) is { } user)
        {
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            emails.PasswordReset(user, request.Language, WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)));
        }

        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Sets the new password from a reset link. Signs out every device; the user then signs in normally (with two-factor sign-in, if on).</summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> ResetPassword(
        ResetPasswordRequest request, UserManager<AppUser> userManager, MailnixDbContext db, AccountEmails emails, CancellationToken ct)
    {
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (FormatException)
        {
            return ResetLinkInvalid();
        }

        if (await userManager.FindByEmailAsync(request.Email.Trim()) is not { } user)
        {
            return ResetLinkInvalid();
        }

        var result = await userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? ResetLinkInvalid()
                : TypedResults.ValidationProblem(ToValidationErrors(result));
        }

        // Whoever got locked out by wrong guesses can sign in again with the new password.
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        await ApiTokenEndpoints.RevokeAllAsync(db, user.Id, ct);
        emails.PasswordChanged(user);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult ResetLinkInvalid() =>
        Problem(StatusCodes.Status400BadRequest, "ResetLinkInvalid", "This link is no longer valid. Please ask for a new one.");

    private static async Task<Results<Ok<InvitationInfo>, NotFound>> GetInvitation(string token, MailnixDbContext db, TimeProvider time) =>
        await FindInvitationAsync(db, token, time) is { } found
            ? TypedResults.Ok(new InvitationInfo(found.TenantName, found.Invitation.Email))
            : TypedResults.NotFound();

    private static async Task<(Invitation Invitation, string TenantName)?> FindInvitationAsync(MailnixDbContext db, string token, TimeProvider time)
    {
        var hash = SecretTokens.Hash(token);
        var found = await db.Invitations
            .Where(i => i.TokenHash == hash && i.AcceptedAt == null)
            .Join(db.Tenants, i => i.TenantId, t => t.Id, (i, t) => new { Invitation = i, t.Name })
            .SingleOrDefaultAsync();
        return found is null || found.Invitation.ExpiresAt < time.GetUtcNow() ? null : (found.Invitation, found.Name);
    }

    private static async Task<Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult>> Register(
        RegisterRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        MailnixDbContext db,
        IOptions<AuthOptions> options,
        IOptions<TenancyOptions> tenancy,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var isFirstUser = !await userManager.Users.AnyAsync();
        Invitation? invitation = null;
        if (!string.IsNullOrWhiteSpace(request.InviteToken))
        {
            if (await FindInvitationAsync(db, request.InviteToken, timeProvider) is not { } found)
            {
                return Problem(StatusCodes.Status400BadRequest, "InvitationInvalid", "This invitation is no longer valid.");
            }

            if (found.Invitation.Email is { } invited && !invited.Equals(request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Problem(StatusCodes.Status400BadRequest, "InvitationForOtherEmail", $"This invitation is for {invited}.");
            }

            invitation = found.Invitation;
        }
        else if (!options.Value.AllowRegistration && !isFirstUser)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Registration is disabled.");
        }

        // Self-hosted: one tenant for everybody. SaaS: each signup gets its own tenant, unless invited into one.
        var multiTenant = tenancy.Value.Mode == TenancyMode.MultiTenant;
        var tenantId = invitation?.TenantId ?? Tenant.DefaultId;
        Tenant? newTenant = null;
        if (invitation is null && multiTenant && !isFirstUser)
        {
            newTenant = new Tenant
            {
                Id = Guid.CreateVersion7(),
                Name = string.IsNullOrWhiteSpace(request.OrganizationName) ? request.Email.Trim() : request.OrganizationName.Trim(),
                MaxAccounts = tenancy.Value.DefaultMaxAccounts,
                CreatedAt = now,
            };
            db.Tenants.Add(newTenant);
            tenantId = newTenant.Id;
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            TenantId = tenantId,
            CreatedAt = now,

            // From the browser, so date folders and schedules match the user's day from the start.
            TimeZone = UserPreferences.IsTimeZone(request.TimeZone) ? request.TimeZone! : "UTC",
            Language = UserPreferences.IsLanguage(request.Language) ? request.Language : null,
        };

        // The tenant is saved together with the user (UserManager saves the context).
        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            if (newTenant is not null)
            {
                db.Tenants.Remove(newTenant);
            }

            return TypedResults.ValidationProblem(ToValidationErrors(created));
        }

        if (invitation is not null)
        {
            invitation.AcceptedAt = now;
            invitation.AcceptedByUserId = user.Id;
            await db.SaveChangesAsync();
        }

        // The first account administers the installation (and the default tenant); the founder of a SaaS tenant
        // administers that tenant; invited users and later self-hosted signups are plain users.
        string[] roles = isFirstUser ? [Roles.Admin, Roles.TenantAdmin, Roles.User]
            : invitation is null && multiTenant ? [Roles.TenantAdmin, Roles.User]
            : [Roles.User];
        var assigned = await userManager.AddToRolesAsync(user, roles);
        if (!assigned.Succeeded)
        {
            return TypedResults.ValidationProblem(ToValidationErrors(assigned));
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return TypedResults.Ok(await ToMeResponseAsync(user, userManager));
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> Login(
        LoginRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        var result = await signInManager.PasswordSignInAsync(request.Email, request.Password, request.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded && await userManager.FindByEmailAsync(request.Email) is { } user)
        {
            return TypedResults.Ok(await ToMeResponseAsync(user, userManager));
        }

        if (result.RequiresTwoFactor)
        {
            // Password accepted; a short-lived cookie remembers that for the second step.
            return Problem(StatusCodes.Status401Unauthorized, "TwoFactorRequired", "Enter the code from your authenticator app.");
        }

        var title = result.IsLockedOut ? "Account is temporarily locked. Try again later." : "Invalid email or password.";
        return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: title);
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> LoginTwoFactor(
        TwoFactorLoginRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager)
    {
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is not { } user)
        {
            return Problem(StatusCodes.Status401Unauthorized, "TwoFactorExpired", "Sign in again with your password.");
        }

        var result = request.IsRecoveryCode
            ? await signInManager.TwoFactorRecoveryCodeSignInAsync(NormalizeRecoveryCode(request.Code))
            : await signInManager.TwoFactorAuthenticatorSignInAsync(ProfileEndpoints.NormalizeCode(request.Code), request.RememberMe, rememberClient: false);
        if (result.Succeeded)
        {
            return TypedResults.Ok(await ToMeResponseAsync(user, userManager));
        }

        return result.IsLockedOut
            ? TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Account is temporarily locked. Try again later.")
            : Problem(StatusCodes.Status401Unauthorized, "InvalidCode", "The code is not valid.");
    }

    /// <summary>Recovery codes are stored as "ABCDE-FGHIJ"; accept them typed without the dash or in lowercase.</summary>
    private static string NormalizeRecoveryCode(string code)
    {
        var compact = code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return compact.Length == 10 ? $"{compact[..5]}-{compact[5..]}" : compact;
    }

    private static async Task<NoContent> Logout(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> Me(ClaimsPrincipal principal, UserManager<AppUser> userManager)
    {
        // The cookie can outlive the account (e.g. user deleted by an admin).
        var user = await userManager.GetUserAsync(principal);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(await ToMeResponseAsync(user, userManager));
    }

    private static async Task<MeResponse> ToMeResponseAsync(AppUser user, UserManager<AppUser> userManager) =>
        new(user.Id, user.Email!, user.DisplayName, [.. await userManager.GetRolesAsync(user)], user.TimeZone, user.Language);

    private static ProblemHttpResult Problem(int status, string errorCode, string title) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });

    private static Dictionary<string, string[]> ToValidationErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
