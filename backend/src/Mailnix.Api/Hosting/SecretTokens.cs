using System.Security.Cryptography;
using System.Text;

namespace Mailnix.Api.Hosting;

/// <summary>Random tokens that are shown once (invitation links, API tokens); only their hash is stored.</summary>
internal static class SecretTokens
{
    public static string Create(string prefix = "") =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
}
