using System.ComponentModel.DataAnnotations;

namespace Mailnix.Api.Auth;

/// <param name="RegistrationOpen">Anyone may sign up (invitations work regardless).</param>
/// <param name="MultiTenant">Signing up creates an own tenant (SaaS).</param>
public sealed record AuthConfigResponse(bool RegistrationOpen, bool MultiTenant);

/// <summary>What an invitation link leads to, shown on the signup page.</summary>
public sealed record InvitationInfo(string TenantName, string? Email);

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(10), MaxLength(256)] string Password,
    [MaxLength(200)] string? DisplayName = null,
    [MaxLength(200)] string? OrganizationName = null,
    [MaxLength(200)] string? InviteToken = null);

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MaxLength(256)] string Password,
    bool RememberMe);

public sealed record MeResponse(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    string TimeZone,
    string Language);
