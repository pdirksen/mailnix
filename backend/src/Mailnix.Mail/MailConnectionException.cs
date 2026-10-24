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
