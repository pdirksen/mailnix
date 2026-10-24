using System.Security.Claims;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Auth;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth").WithTags("Auth");

        group.MapGet("/config", GetConfig).AllowAnonymous();
        group.MapPost("/register", Register).AllowAnonymous();
        group.MapPost("/login", Login).AllowAnonymous();
        group.MapPost("/logout", Logout);
        group.MapGet("/me", Me);

        return group;
    }

    private static async Task<Ok<AuthConfigResponse>> GetConfig(UserManager<AppUser> userManager, IOptions<AuthOptions> options) =>
        TypedResults.Ok(new AuthConfigResponse(options.Value.AllowRegistration || !await userManager.Users.AnyAsync()));

    private static async Task<Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult>> Register(
        RegisterRequest request,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        IOptions<AuthOptions> options,
        TimeProvider timeProvider)
    {
        var isFirstUser = !await userManager.Users.AnyAsync();
        if (!options.Value.AllowRegistration && !isFirstUser)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Registration is disabled.");
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            TenantId = Tenant.DefaultId,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return TypedResults.ValidationProblem(ToValidationErrors(created));
        }

        // Without a bootstrap admin, the first account administers the installation.
        string[] roles = isFirstUser ? [Roles.Admin, Roles.User] : [Roles.User];
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

        var title = result switch
        {
            { IsLockedOut: true } => "Account is temporarily locked. Try again later.",
            { RequiresTwoFactor: true } => "Two-factor authentication required.",
            _ => "Invalid email or password.",
        };
        return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: title);
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

    private static Dictionary<string, string[]> ToValidationErrors(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
