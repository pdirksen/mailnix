namespace Mailnix.Mail;

public enum ImapSecurity
{
    /// <summary>Implicit TLS, usually port 993.</summary>
    SslOnConnect,

    /// <summary>Plain connection upgraded via STARTTLS, usually port 143.</summary>
    StartTls,

    /// <summary>Unencrypted. Only allowed when <see cref="MailOptions.AllowInsecureConnections"/> is set.</summary>
    None,
}

public sealed record ImapConnectionSettings(string Host, int Port, ImapSecurity Security, string Username, string Password)
{
    public static int DefaultPort(ImapSecurity security) => security == ImapSecurity.SslOnConnect ? 993 : 143;

    // Records print every property by default; keep the password out of logs and exception messages.
    public override string ToString() => $"{Username}@{Host}:{Port} ({Security})";
}
