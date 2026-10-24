using System.Security.Cryptography;
using System.Text.Json;
using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;

namespace Mailnix.Infrastructure.OAuth;

/// <param name="AccountId">Set when reconnecting an existing account.</param>
/// <param name="Name">Name for a new account.</param>
public sealed record OAuthState(Guid UserId, MailProvider Provider, string CodeVerifier, string RedirectUri, Guid? AccountId, string? Name);

/// <summary>
/// The OAuth "state" parameter: everything the callback needs, encrypted and signed (data protection) and valid
/// for a limited time. Nothing is kept on the server, so any API instance can complete the sign-in.
/// </summary>
public sealed class OAuthStateProtector(IDataProtectionProvider provider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("Mailnix.OAuth.State.v1").ToTimeLimitedDataProtector();

    public string Protect(OAuthState state) => _protector.Protect(JsonSerializer.Serialize(state), Lifetime);

    /// <summary>The state, or null when it was tampered with, expired or comes from elsewhere.</summary>
    public OAuthState? Unprotect(string protectedState)
    {
        try
        {
            return JsonSerializer.Deserialize<OAuthState>(_protector.Unprotect(protectedState));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}
