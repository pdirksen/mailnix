using System.ComponentModel.DataAnnotations;

namespace Mailnix.Api.Profile;

/// <param name="Addresses">Further addresses the user added (aliases, forwarding targets).</param>
/// <param name="AutomaticAddresses">Addresses that always count as the user's: the sign-in email and the mailbox user names.</param>
public sealed record ProfileResponse(
    string Email,
    string? DisplayName,
    string TimeZone,
    string? Language,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> AutomaticAddresses,
    string OrganizationName,
    bool MultiTenant,
    TwoFactorStatus TwoFactor,
    DateTimeOffset CreatedAt);

/// <param name="RecoveryCodesLeft">Unused recovery codes (0 when two-factor sign-in is off).</param>
public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

/// <summary>Only the given fields change; an empty display name clears it.</summary>
public sealed record UpdateProfileRequest(
    [MaxLength(200)] string? DisplayName = null,
    [MaxLength(100)] string? TimeZone = null,
    [MaxLength(10)] string? Language = null);

public sealed record UpdateAddressesRequest([Required] IReadOnlyList<string> Addresses);

public sealed record ChangePasswordRequest(
    [Required, MaxLength(256)] string CurrentPassword,
    [Required, MinLength(10), MaxLength(256)] string NewPassword);

/// <summary>The current password, to confirm a sensitive change.</summary>
public sealed record PasswordConfirmation([Required, MaxLength(256)] string Password);

/// <param name="SharedKey">The secret for typing into the authenticator app, in groups of four.</param>
/// <param name="AuthenticatorUri">otpauth:// URI for the QR code.</param>
public sealed record AuthenticatorSetup(string SharedKey, string AuthenticatorUri);

public sealed record EnableTwoFactorRequest([Required, MaxLength(20)] string Code);

/// <param name="Codes">Shown only now; each signs in once instead of an authenticator code.</param>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> Codes);
