namespace Mailnix.Mail;

/// <summary>Why connecting to a mailbox failed. Stable codes the UI can translate.</summary>
public enum ConnectionErrorCode
{
    HostNotFound,
    ConnectionRefused,
    ConnectionFailed,
    Timeout,
    TlsFailed,
    AuthenticationFailed,
    ProtocolError,
    InsecureConnectionNotAllowed,
    AddressNotAllowed,

    /// <summary>The OAuth authorization was revoked or has expired; the user has to sign in with the provider again.</summary>
    ReconnectRequired,
    Unknown,
}

public sealed class MailConnectionException : Exception
{
    public MailConnectionException()
        : this(ConnectionErrorCode.Unknown, "Connecting to the mail server failed.")
    {
    }

    public MailConnectionException(string message)
        : this(ConnectionErrorCode.Unknown, message)
    {
    }

    public MailConnectionException(string message, Exception innerException)
        : this(ConnectionErrorCode.Unknown, message, innerException)
    {
    }

    public MailConnectionException(ConnectionErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ConnectionErrorCode Code { get; }
}

public static class MailConnectionErrors
{
    /// <summary>Turns a MailKit/socket exception into a classified <see cref="MailConnectionException"/>.</summary>
    public static MailConnectionException Classify(Exception exception) => ImapConnector.Classify(exception, timedOut: false);
}
