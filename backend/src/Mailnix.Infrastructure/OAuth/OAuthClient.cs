using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Mailnix.Infrastructure.OAuth;

/// <summary>What mailnix needs to know about an OAuth mail provider.</summary>
public sealed record OAuthProvider(
    MailProvider Provider,
    string ClientId,
    string ClientSecret,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string Scopes,
    IReadOnlyDictionary<string, string> ExtraAuthorizationParameters,
    string ImapHost,
    int ImapPort,
    ImapSecurity ImapSecurity);

/// <param name="RefreshToken">Null when the provider keeps the previous one (no rotation).</param>
/// <param name="Email">The mailbox address from the ID token; only on the first token request.</param>
public sealed record OAuthTokens(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string? Email);

public sealed class OAuthException(string error, string? description)
    : Exception(string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}")
{
    public string Error { get; } = error;

    /// <summary>The grant was revoked or has expired: only signing in again helps.</summary>
    public bool ReauthorizationRequired => Error == "invalid_grant";
}

/// <summary>Authorization code flow with PKCE for Google and Microsoft (OAuth 2.0 / OpenID Connect), plus refresh.</summary>
public sealed class OAuthClient(HttpClient http, IOptions<OAuthOptions> options, TimeProvider time)
{
    /// <summary>Google sends "email"; Microsoft "email" only when configured, but always "preferred_username".</summary>
    private static readonly string[] EmailClaims = ["email", "preferred_username", "upn"];

    public bool IsConfigured(MailProvider provider) => provider switch
    {
        MailProvider.Google => options.Value.Google.IsConfigured,
        MailProvider.Microsoft => options.Value.Microsoft.IsConfigured,
        _ => false,
    };

    public OAuthProvider Get(MailProvider provider)
    {
        if (!IsConfigured(provider))
        {
            throw new InvalidOperationException($"OAuth for {provider} is not configured ({OAuthOptions.Section}:{provider}:ClientId and ClientSecret).");
        }

        switch (provider)
        {
            case MailProvider.Google:
                var google = options.Value.Google;
                return new OAuthProvider(
                    provider,
                    google.ClientId!,
                    google.ClientSecret!,
                    google.AuthorizationEndpoint ?? "https://accounts.google.com/o/oauth2/v2/auth",
                    google.TokenEndpoint ?? "https://oauth2.googleapis.com/token",
                    "https://mail.google.com/ openid email",
                    // A refresh token only comes with offline access, and reliably only with a fresh consent.
                    new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" },
                    google.ImapHost ?? "imap.gmail.com",
                    google.ImapPort ?? 993,
                    google.ImapSecurity ?? ImapSecurity.SslOnConnect);
            default:
                var microsoft = options.Value.Microsoft;
                var authority = $"https://login.microsoftonline.com/{Uri.EscapeDataString(microsoft.Tenant)}/oauth2/v2.0";
                return new OAuthProvider(
                    provider,
                    microsoft.ClientId!,
                    microsoft.ClientSecret!,
                    microsoft.AuthorizationEndpoint ?? $"{authority}/authorize",
                    microsoft.TokenEndpoint ?? $"{authority}/token",
                    "https://outlook.office.com/IMAP.AccessAsUser.All offline_access openid email profile",
                    new Dictionary<string, string> { ["prompt"] = "select_account" },
                    microsoft.ImapHost ?? "outlook.office365.com",
                    microsoft.ImapPort ?? 993,
                    microsoft.ImapSecurity ?? ImapSecurity.SslOnConnect);
        }
    }

    /// <summary>A random PKCE code verifier (RFC 7636).</summary>
    public static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string CodeChallenge(string verifier) => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public string AuthorizationUrl(MailProvider provider, string redirectUri, string state, string codeVerifier, string? loginHint = null)
    {
        var p = Get(provider);
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = p.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = p.Scopes,
            ["state"] = state,
            ["code_challenge"] = CodeChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = loginHint,
        };
        foreach (var (key, value) in p.ExtraAuthorizationParameters)
        {
            query[key] = value;
        }

        return QueryHelpers.AddQueryString(p.AuthorizationEndpoint, query.Where(q => q.Value is not null));
    }

    public Task<OAuthTokens> ExchangeCodeAsync(MailProvider provider, string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var p = Get(provider);
        return RequestAsync(p, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct);
    }

    public Task<OAuthTokens> RefreshAsync(MailProvider provider, string refreshToken, CancellationToken ct)
    {
        var p = Get(provider);
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken };
        if (provider == MailProvider.Microsoft)
        {
            form["scope"] = p.Scopes;
        }

        return RequestAsync(p, form, ct);
    }

    private async Task<OAuthTokens> RequestAsync(OAuthProvider provider, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = provider.ClientId;
        form["client_secret"] = provider.ClientSecret;
        var requestedAt = time.GetUtcNow();
        using var response = await http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(form), ct);

        JsonElement json;
        try
        {
            json = await response.Content.ReadFromJsonElementAsync(ct);
        }
        catch (JsonException)
        {
            throw new OAuthException("invalid_response", $"The token endpoint answered {(int)response.StatusCode} without JSON.");
        }

        if (!response.IsSuccessStatusCode || !json.TryGetProperty("access_token", out var accessToken))
        {
            throw new OAuthException(
                Text(json, "error") ?? $"http_{(int)response.StatusCode}",
                Text(json, "error_description"));
        }

        var expiresIn = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
        return new OAuthTokens(
            accessToken.GetString()!,
            requestedAt.AddSeconds(expiresIn),
            Text(json, "refresh_token"),
            Text(json, "id_token") is { } idToken ? EmailFromIdToken(idToken) : null);
    }

    /// <summary>
    /// The mailbox address from an ID token. It came straight from the token endpoint over TLS, which OpenID Connect
    /// accepts in place of checking the signature (Core 1.0, 3.1.3.7).
    /// </summary>
    internal static string? EmailFromIdToken(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using var claims = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return EmailClaims
                .Select(name => Text(claims.RootElement, name))
                .FirstOrDefault(value => value is not null && value.Contains('@', StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }
}

internal static class HttpContentJsonExtensions
{
    public static async Task<JsonElement> ReadFromJsonElementAsync(this HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }
}
