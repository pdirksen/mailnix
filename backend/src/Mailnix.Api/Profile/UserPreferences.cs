using System.Net.Mail;

namespace Mailnix.Api.Profile;

/// <summary>Validation of what users set about themselves.</summary>
internal static class UserPreferences
{
    public static readonly IReadOnlyList<string> Languages = ["en", "de"];

    /// <summary>Own addresses a user can add at most.</summary>
    public const int MaxAddresses = 50;

    /// <summary>An IANA time zone (like the browser reports) that this server knows.</summary>
    public static bool IsTimeZone(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)
        && (id == "UTC" || TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _));

    public static bool IsLanguage(string? language) => language is not null && Languages.Contains(language);

    /// <summary>A plain address (no display name), lowercase; null when it is not one.</summary>
    public static string? NormalizeAddress(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 254
            && MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed && address.Host.Contains('.', StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : null;
    }
}
