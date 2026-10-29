using System.ComponentModel.DataAnnotations;

namespace Mailnix.Api.Auth;

/// <param name="RegistrationOpen">Anyone may sign up (invitations work regardless).</param>
/// <param name="MultiTenant">Signing up creates an own tenant (SaaS).</param>
/// <param name="PasswordResetAvailable">"Forgot password" works (email and the public address are configured).</param>
/// <param name="SetupCodeRequired">No account exists yet, and the first sign-up needs the setup code from the server log.</param>
public sealed record AuthConfigResponse(bool RegistrationOpen, bool MultiTenant, bool PasswordResetAvailable, bool SetupCodeRequired);

/// <param name="Language">The UI language, for the email when the account has none chosen.</param>
public sealed record ForgotPasswordRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [MaxLength(10)] string? Language = null);

/// <param name="Token">From the link in the email.</param>
public sealed record ResetPasswordRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(2048)] string Token,
    [Required, MinLength(10), MaxLength(256)] string NewPassword);

/// <summary>What an invitation link leads to, shown on the signup page.</summary>
public sealed record InvitationInfo(string TenantName, string? Email);

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(10), MaxLength(256)] string Password,
    [MaxLength(200)] string? DisplayName = null,
    [MaxLength(200)] string? OrganizationName = null,
    [MaxLength(200)] string? InviteToken = null,
    [MaxLength(100)] string? TimeZone = null,
    [MaxLength(10)] string? Language = null,
    [MaxLength(100)] string? SetupCode = null);

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password,
    bool RememberMe);

/// <summary>Second step of signing in with two-factor sign-in: a code from the authenticator app or a recovery code.</summary>
public sealed record TwoFactorLoginRequest(
    [Required, MaxLength(30)] string Code,
    bool IsRecoveryCode,
    bool RememberMe);

public sealed record MeResponse(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    string TimeZone,
    string? Language);
