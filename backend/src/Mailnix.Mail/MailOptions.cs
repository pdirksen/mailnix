namespace Mailnix.Mail;

public sealed class MailOptions
{
    public const string Section = "Mailnix:Mail";

    /// <summary>Upper bound for DNS lookup, TCP/TLS connect and authentication together.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for a single IMAP command once connected.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Permit unencrypted IMAP (passwords in clear text). Off by default.</summary>
    public bool AllowInsecureConnections { get; set; }

    /// <summary>
    /// Refuse mail servers on loopback, private, link-local and similar addresses. Recommended for multi-tenant
    /// (SaaS) deployments so users cannot probe the internal network; self-hosted setups often need LAN servers.
    /// </summary>
    public bool BlockPrivateNetworks { get; set; }
}
