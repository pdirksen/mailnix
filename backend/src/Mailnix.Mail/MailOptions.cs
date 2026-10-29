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
    /// Refuse mail servers on loopback, private, link-local and similar addresses, so users cannot probe the internal
    /// network. Unset: on in multi-tenant (SaaS) installations, off otherwise (self-hosted setups often use LAN servers).
    /// </summary>
    public bool? BlockPrivateNetworks { get; set; }
}
