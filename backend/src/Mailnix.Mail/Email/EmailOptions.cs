namespace Mailnix.Mail.Email;

public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded via STARTTLS, usually port 587.</summary>
    StartTls,

    /// <summary>Implicit TLS, usually port 465.</summary>
    SslOnConnect,

    /// <summary>Unencrypted, e.g. a relay on the local network (port 25).</summary>
    None,
}

/// <summary>The SMTP server mailnix sends its own emails through (password reset, invitations, notices).</summary>
public sealed class EmailOptions
{
    public const string Section = "Mailnix:Email";

    public string? Host { get; set; }

    public int? Port { get; set; }

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Leave empty for servers that accept mail without signing in (local relays).</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Sender address, e.g. mailnix@example.com.</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "mailnix";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Invitations sent by email per organization within 24 hours. Every SaaS signup administers its own
    /// organization, so without a limit anyone could send mass mail through this server.
    /// </summary>
    public int InvitationsPerDay { get; set; } = 20;

    /// <summary>Email is switched on once a server and a sender address are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    public int EffectivePort => Port ?? Security switch
    {
        SmtpSecurity.SslOnConnect => 465,
        SmtpSecurity.None => 25,
        _ => 587,
    };
}
