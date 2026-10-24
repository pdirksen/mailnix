using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Mailnix.Integration.Tests.Fakes;

/// <summary>A token endpoint like Google's or Microsoft's: code exchange (with PKCE check) and refresh.</summary>
internal sealed class FakeOAuthProvider : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _challenges = [];

    public string Email { get; set; } = "user@example.com";

    /// <summary>The ID token claim carrying the address ("email" for Google, "preferred_username" for Microsoft).</summary>
    public string EmailClaim { get; set; } = "email";

    public string AccessToken { get; set; } = "access-1";

    public string RefreshToken { get; set; } = "refresh-1";

    /// <summary>Returned on refresh when set (Microsoft rotates refresh tokens).</summary>
    public string? RotatedRefreshToken { get; set; }

    public int ExpiresIn { get; set; } = 3600;

    public bool Revoked { get; set; }

    public bool WithoutRefreshToken { get; set; }

    public List<Dictionary<string, string>> Requests { get; } = [];

    /// <summary>Remembers the PKCE challenge of an authorization URL, as the provider would after the user's consent.</summary>
    public string Authorize(string authorizationUrl)
    {
        var query = QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query);
        var code = $"code-{Guid.NewGuid():N}";
        lock (_lock)
        {
            _challenges[code] = query["code_challenge"]!;
        }

        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(p => p.Key, p => p.Value.ToString());
        lock (_lock)
        {
            Requests.Add(form);
        }

        if (form.GetValueOrDefault("client_secret") != "test-secret")
        {
            return Error("invalid_client");
        }

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                string? challenge;
                lock (_lock)
                {
                    _challenges.Remove(form.GetValueOrDefault("code") ?? string.Empty, out challenge);
                }

                var verifier = form.GetValueOrDefault("code_verifier") ?? string.Empty;
                var computed = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (challenge is null || challenge != computed)
                {
                    return Error("invalid_grant", "Bad code or code verifier.");
                }

                return Json(new Dictionary<string, object?>
                {
                    ["access_token"] = AccessToken,
                    ["expires_in"] = ExpiresIn,
                    ["refresh_token"] = WithoutRefreshToken ? null : RefreshToken,
                    ["id_token"] = IdToken(),
                });

            case "refresh_token":
                if (Revoked || form.GetValueOrDefault("refresh_token") != RefreshToken)
                {
                    return Error("invalid_grant", "Token has been expired or revoked.");
                }

                if (RotatedRefreshToken is { } rotated)
                {
                    RefreshToken = rotated;
                }

                return Json(new Dictionary<string, object?> { ["access_token"] = AccessToken, ["expires_in"] = ExpiresIn, ["refresh_token"] = RotatedRefreshToken });

            default:
                return Error("unsupported_grant_type");
        }
    }

    private string IdToken()
    {
        static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part(new { alg = "RS256" })}.{Part(new Dictionary<string, string> { [EmailClaim] = Email, ["sub"] = "123" })}.signature";
    }

    private static HttpResponseMessage Json(Dictionary<string, object?> body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body.Where(p => p.Value is not null).ToDictionary()), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string error, string? description = null) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error, error_description = description }), Encoding.UTF8, "application/json") };
}
