using System.Security.Cryptography;
using System.Text;

namespace Mailnix.Api.Team;

/// <summary>Invitation tokens: random, shown once in the link; only their hash is stored.</summary>
internal static class InvitationTokens
{
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}
