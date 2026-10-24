using System.ComponentModel.DataAnnotations;

namespace Mailnix.Api.Auth;

public sealed record AuthConfigResponse(bool RegistrationOpen);

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(10), MaxLength(256)] string Password,
    [MaxLength(200)] string? DisplayName = null);

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
