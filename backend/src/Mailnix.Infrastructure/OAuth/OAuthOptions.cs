using Mailnix.Mail;

namespace Mailnix.Infrastructure.OAuth;

/// <summary>OAuth apps for mailbox access; each self-hosted installation registers its own (see docs/oauth-setup.md).</summary>
public class OAuthOptions
{
    public const string Section = "Mailnix:OAuth";

    /// <summary>
    /// Public address of mailnix (e.g. https://mail.example.com) for the redirect URI {RedirectBaseUrl}/oauth/callback.
    /// Defaults to the address the browser used, which is right unless a proxy rewrites the host.
    /// </summary>
    public string? RedirectBaseUrl { get; set; }

    public OAuthProviderOptions Google { get; set; } = new();

    public MicrosoftOAuthOptions Microsoft { get; set; } = new();
}

public class OAuthProviderOptions
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Overrides for tests or special setups; the provider's defaults otherwise.</summary>
    public string? AuthorizationEndpoint { get; set; }

    public string? TokenEndpoint { get; set; }

    public string? ImapHost { get; set; }

    public int? ImapPort { get; set; }

    public ImapSecurity? ImapSecurity { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public class MicrosoftOAuthOptions : OAuthProviderOptions
{
    /// <summary>"common" (work, school and personal accounts), "organizations", "consumers" or a tenant id.</summary>
    public string Tenant { get; set; } = "common";
}
