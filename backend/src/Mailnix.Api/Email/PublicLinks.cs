using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.OAuth;
using Microsoft.Extensions.Options;

namespace Mailnix.Api.Email;

/// <summary>
/// Links to mailnix in emails. They use the configured public address only, never the request's Host header: a
/// password-reset link built from a forged header would send the reset token to someone else's site.
/// </summary>
public sealed class PublicLinks(IOptions<MailnixHostOptions> host, IOptions<OAuthOptions> oauth)
{
    /// <summary>Mailnix:PublicUrl, else OAuth:RedirectBaseUrl; null when neither is set.</summary>
    public string? BaseUrl =>
        (string.IsNullOrWhiteSpace(host.Value.PublicUrl) ? oauth.Value.RedirectBaseUrl : host.Value.PublicUrl)?.Trim().TrimEnd('/') is { Length: > 0 } url
            ? url
            : null;

    public bool IsConfigured => BaseUrl is not null;

    /// <summary>An absolute link to an SPA route, e.g. /reset-password, with query parameters.</summary>
    public string For(string path, IReadOnlyDictionary<string, string>? query = null)
    {
        var baseUrl = BaseUrl ?? throw new InvalidOperationException("Mailnix:PublicUrl is not configured.");
        var link = $"{baseUrl}{path}";
        return query is { Count: > 0 }
            ? $"{link}?{string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"))}"
            : link;
    }
}
