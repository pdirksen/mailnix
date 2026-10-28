using System.Globalization;
using System.Security.Claims;
using System.Text;
using Mailnix.Api.Email;
using Mailnix.Api.Hosting;
using Mailnix.Api.Mcp;
using Mailnix.Api.Team;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Mailnix.Infrastructure.Options;
using Mailnix.Worker;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Profile;

/// <summary>The signed-in user's own settings: profile, preferences, addresses, password and two-factor sign-in.</summary>
internal static class ProfileEndpoints
{
    private const int RecoveryCodeCount = 10;

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/profile").WithTags("Profile");
        group.MapGet("/", Get);
        group.MapPut("/", Update);
        group.MapPut("/addresses", UpdateAddresses);
        group.MapPost("/sign-out-others", SignOutOthers);

        // Everything that checks the password or a code is limited like sign-in (against guessing).
        group.MapPost("/password", ChangePassword).RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/two-factor/setup", SetUpAuthenticator);
        group.MapPost("/two-factor/enable", EnableTwoFactor).RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/two-factor/disable", DisableTwoFactor).RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/two-factor/recovery-codes", NewRecoveryCodes).RequireRateLimiting(RateLimiting.Auth);
        group.MapPost("/delete", DeleteAccount).RequireRateLimiting(RateLimiting.Auth);
        return routes;
    }

    private static async Task<Results<Ok<ProfileResponse>, UnauthorizedHttpResult>> Get(
        ClaimsPrincipal principal, MailnixDbContext db, UserManager<AppUser> users, IOptions<TenancyOptions> tenancy, CancellationToken ct) =>
        await users.GetUserAsync(principal) is { } user
            ? TypedResults.Ok(await ToResponseAsync(user, db, users, tenancy.Value, ct))
            : TypedResults.Unauthorized();

    private static async Task<Results<Ok<ProfileResponse>, ValidationProblem, UnauthorizedHttpResult>> Update(
        UpdateProfileRequest request, ClaimsPrincipal principal, MailnixDbContext db, UserManager<AppUser> users, IOptions<TenancyOptions> tenancy, CancellationToken ct)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        if (request.TimeZone is not null && !UserPreferences.IsTimeZone(request.TimeZone))
        {
            errors["timeZone"] = ["Unknown time zone."];
        }

        if (request.Language is not null && !UserPreferences.IsLanguage(request.Language))
        {
            errors["language"] = [$"Supported languages: {string.Join(", ", UserPreferences.Languages)}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (request.DisplayName is not null)
        {
            user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        }

        user.TimeZone = request.TimeZone ?? user.TimeZone;
        user.Language = request.Language ?? user.Language;
        await users.UpdateAsync(user);
        return TypedResults.Ok(await ToResponseAsync(user, db, users, tenancy.Value, ct));
    }

    private static async Task<Results<Ok<ProfileResponse>, ValidationProblem, UnauthorizedHttpResult>> UpdateAddresses(
        UpdateAddressesRequest request, ClaimsPrincipal principal, MailnixDbContext db, UserManager<AppUser> users, IOptions<TenancyOptions> tenancy, CancellationToken ct)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var invalid = request.Addresses.Where(a => UserPreferences.NormalizeAddress(a) is null).ToList();
        if (invalid.Count > 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["addresses"] = [.. invalid.Select(a => $"Not an email address: {a}")] });
        }

        // The automatic ones count anyway; keeping them out avoids duplicates.
        var automatic = await AutomaticAddressesAsync(user, db, ct);
        var addresses = request.Addresses.Select(a => UserPreferences.NormalizeAddress(a)!).Distinct().Where(a => !automatic.Contains(a)).Order(StringComparer.Ordinal).ToList();
        if (addresses.Count > UserPreferences.MaxAddresses)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["addresses"] = [$"At most {UserPreferences.MaxAddresses} addresses."] });
        }

        user.Addresses = addresses;
        await users.UpdateAsync(user);
        return TypedResults.Ok(await ToResponseAsync(user, db, users, tenancy.Value, ct));
    }

    /// <summary>
    /// Other browsers and devices are signed out within a minute (when their cookie is next checked); MCP tokens and
    /// connected apps stop working at once.
    /// </summary>
    private static async Task<Results<NoContent, UnauthorizedHttpResult>> SignOutOthers(
        ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn, MailnixDbContext db, CancellationToken ct)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        await users.UpdateSecurityStampAsync(user);
        await signIn.RefreshSignInAsync(user);
        await ApiTokenEndpoints.RevokeAllAsync(db, user.Id, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> ChangePassword(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        MailnixDbContext db,
        AccountEmails emails,
        CancellationToken ct)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
                ? WrongPassword()
                : TypedResults.ValidationProblem(result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
        }

        // A new security stamp signs out other devices (and the MCP tokens); this one stays signed in.
        await signIn.RefreshSignInAsync(user);
        await ApiTokenEndpoints.RevokeAllAsync(db, user.Id, ct);
        emails.PasswordChanged(user);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<AuthenticatorSetup>, ProblemHttpResult, UnauthorizedHttpResult>> SetUpAuthenticator(ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return Problem(StatusCodes.Status409Conflict, "TwoFactorAlreadyEnabled", "Two-factor sign-in is already on. Switch it off first to use another app.");
        }

        // A fresh key each time, so an abandoned setup never stays usable.
        await users.ResetAuthenticatorKeyAsync(user);
        await signIn.RefreshSignInAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var label = Uri.EscapeDataString($"mailnix:{user.Email}");
        return TypedResults.Ok(new AuthenticatorSetup(
            FormatKey(key),
            $"otpauth://totp/{label}?secret={key}&issuer=mailnix&digits=6"));
    }

    private static async Task<Results<Ok<RecoveryCodesResponse>, ProblemHttpResult, UnauthorizedHttpResult>> EnableTwoFactor(
        EnableTwoFactorRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountEmails emails)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (user.TwoFactorEnabled)
        {
            return Problem(StatusCodes.Status409Conflict, "TwoFactorAlreadyEnabled", "Two-factor sign-in is already on.");
        }

        if (string.IsNullOrEmpty(await users.GetAuthenticatorKeyAsync(user)))
        {
            return Problem(StatusCodes.Status409Conflict, "TwoFactorSetupMissing", "Start the setup again.");
        }

        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(request.Code)))
        {
            return Problem(StatusCodes.Status400BadRequest, "InvalidCode", "The code is not valid. Check the time on your phone and try the next code.");
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        await signIn.RefreshSignInAsync(user);
        emails.TwoFactorChanged(user, enabled: true);
        return TypedResults.Ok(new RecoveryCodesResponse([.. codes ?? []]));
    }

    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> DisableTwoFactor(
        PasswordConfirmation request, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn, AccountEmails emails)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            return WrongPassword();
        }

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        await signIn.RefreshSignInAsync(user);
        emails.TwoFactorChanged(user, enabled: false);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RecoveryCodesResponse>, ProblemHttpResult, UnauthorizedHttpResult>> NewRecoveryCodes(
        PasswordConfirmation request, ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            return WrongPassword();
        }

        if (!user.TwoFactorEnabled)
        {
            return Problem(StatusCodes.Status409Conflict, "TwoFactorNotEnabled", "Two-factor sign-in is off.");
        }

        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return TypedResults.Ok(new RecoveryCodesResponse([.. codes ?? []]));
    }

    private static async Task<Results<NoContent, ProblemHttpResult, UnauthorizedHttpResult>> DeleteAccount(
        PasswordConfirmation request,
        ClaimsPrincipal principal,
        MailnixDbContext db,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IWorkerSignals signals,
        CancellationToken ct)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            return WrongPassword();
        }

        if (await users.IsInRoleAsync(user, Roles.Admin))
        {
            return Problem(StatusCodes.Status409Conflict, "CannotDeleteAdmin", "The administrator of the installation cannot delete their account.");
        }

        if (await UserRemoval.IsLastTenantAdminAsync(user, db, users, othersMustRemain: true))
        {
            return Problem(StatusCodes.Status409Conflict, "LastTenantAdmin", "Make another member admin of the organization first.");
        }

        await UserRemoval.DeleteAsync(user, db, users, signals, ct);
        await signIn.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<ProfileResponse> ToResponseAsync(AppUser user, MailnixDbContext db, UserManager<AppUser> users, TenancyOptions tenancy, CancellationToken ct)
    {
        var organization = await db.Tenants.Where(t => t.Id == user.TenantId).Select(t => t.Name).SingleAsync(ct);
        return new ProfileResponse(
            user.Email!,
            user.DisplayName,
            user.TimeZone,
            user.Language,
            user.Addresses,
            [.. (await AutomaticAddressesAsync(user, db, ct)).Order(StringComparer.Ordinal)],
            organization,
            tenancy.Mode == TenancyMode.MultiTenant,
            new TwoFactorStatus(user.TwoFactorEnabled, user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0),
            user.CreatedAt);
    }

    private static async Task<HashSet<string>> AutomaticAddressesAsync(AppUser user, MailnixDbContext db, CancellationToken ct)
    {
        var usernames = await db.MailAccounts.Where(a => a.UserId == user.Id).Select(a => a.Username).ToListAsync(ct);
        return [.. usernames.Append(user.Email).Select(UserPreferences.NormalizeAddress).OfType<string>()];
    }

    /// <summary>"JBSW Y3DP EHPK 3PXP": easier to type than one long string.</summary>
    private static string FormatKey(string key)
    {
        var formatted = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            formatted.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return formatted.ToString().TrimEnd().ToUpper(CultureInfo.InvariantCulture);
    }

    /// <summary>Apps show codes as "123 456"; recovery codes may be typed with a dash.</summary>
    internal static string NormalizeCode(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();

    private static ProblemHttpResult WrongPassword() =>
        Problem(StatusCodes.Status400BadRequest, "WrongPassword", "The current password is not correct.");

    private static ProblemHttpResult Problem(int status, string errorCode, string title) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}
