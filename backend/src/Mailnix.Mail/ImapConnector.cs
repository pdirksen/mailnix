using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Mail;

public interface IImapConnector
{
    /// <summary>Connects and authenticates. Throws <see cref="MailConnectionException"/> with a classified error.</summary>
    Task<ImapClient> ConnectAsync(ImapConnectionSettings settings, CancellationToken cancellationToken);
}

internal sealed partial class ImapConnector(IOptions<MailOptions> options, ILogger<ImapConnector> logger) : IImapConnector
{
    public async Task<ImapClient> ConnectAsync(ImapConnectionSettings settings, CancellationToken cancellationToken)
    {
        var mailOptions = options.Value;
        if (settings.Security == ImapSecurity.None && !mailOptions.AllowInsecureConnections)
        {
            throw new MailConnectionException(
                ConnectionErrorCode.InsecureConnectionNotAllowed,
                "Unencrypted connections are disabled on this server. Use SSL/TLS or STARTTLS.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(mailOptions.ConnectTimeout);

        var client = new ImapClient { Timeout = (int)mailOptions.OperationTimeout.TotalMilliseconds };
        Socket? socket = null;
        try
        {
            socket = await ConnectSocketAsync(settings.Host, settings.Port, mailOptions.BlockPrivateNetworks, timeout.Token);

            // MailKit takes ownership of the socket; the host name is still used for SNI and certificate validation.
            var connected = socket;
            socket = null;
            await client.ConnectAsync(connected, settings.Host, settings.Port, ToSocketOptions(settings.Security), timeout.Token);
            if (settings.Authentication == ImapAuthentication.OAuth2)
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.Username, settings.Password), timeout.Token);
            }
            else
            {
                await client.AuthenticateAsync(settings.Username, settings.Password, timeout.Token);
            }
            return client;
        }
        catch (Exception ex) when (ex is not MailConnectionException && !cancellationToken.IsCancellationRequested)
        {
            var error = Classify(ex, timedOut: timeout.IsCancellationRequested);
            LogConnectFailed(settings.Host, settings.Port, error.Code, ex);
            Dispose(client, socket);
            throw error;
        }
        catch
        {
            Dispose(client, socket);
            throw;
        }
    }

    private static async Task<Socket> ConnectSocketAsync(string host, int port, bool blockPrivateNetworks, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (blockPrivateNetworks)
        {
            // Connecting to the vetted addresses ourselves (instead of letting MailKit resolve the name again)
            // closes the DNS-rebinding gap between check and use.
            addresses = [.. addresses.Where(a => !NetworkAddressPolicy.IsNonPublic(a))];
            if (addresses.Length == 0)
            {
                throw new MailConnectionException(
                    ConnectionErrorCode.AddressNotAllowed,
                    $"'{host}' resolves to a private or local network address, which is not allowed on this server.");
            }
        }

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, port, cancellationToken);
                return socket;
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static SecureSocketOptions ToSocketOptions(ImapSecurity security) => security switch
    {
        ImapSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        ImapSecurity.StartTls => SecureSocketOptions.StartTls,
        ImapSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
    };

    internal static MailConnectionException Classify(Exception ex, bool timedOut) => ex switch
    {
        _ when timedOut || ex is TimeoutException =>
            new(ConnectionErrorCode.Timeout, "The mail server did not respond in time.", ex),
        AuthenticationException =>
            new(ConnectionErrorCode.AuthenticationFailed, "The mail server rejected the username or password.", ex),
        SslHandshakeException =>
            new(ConnectionErrorCode.TlsFailed, $"The TLS handshake failed: {FirstLine(ex.InnerException?.Message ?? ex.Message)}", ex),
        NotSupportedException when ex.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) =>
            new(ConnectionErrorCode.TlsFailed, "The mail server does not support STARTTLS.", ex),
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } =>
            new(ConnectionErrorCode.HostNotFound, "The mail server's host name could not be resolved.", ex),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            new(ConnectionErrorCode.ConnectionRefused, "The mail server refused the connection. Check host and port.", ex),
        SocketException { SocketErrorCode: SocketError.TimedOut } =>
            new(ConnectionErrorCode.Timeout, "The mail server did not respond in time.", ex),
        SocketException or IOException =>
            new(ConnectionErrorCode.ConnectionFailed, $"The connection to the mail server failed: {FirstLine(ex.Message)}", ex),
        ImapProtocolException or ImapCommandException or ProtocolException =>
            new(ConnectionErrorCode.ProtocolError, $"The mail server sent an unexpected response: {FirstLine(ex.Message)}", ex),
        _ => new(ConnectionErrorCode.Unknown, $"Connecting to the mail server failed: {FirstLine(ex.Message)}", ex),
    };

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    private static void Dispose(ImapClient client, Socket? socket)
    {
        socket?.Dispose();
        client.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP connection to {Host}:{Port} failed: {Code}")]
    private partial void LogConnectFailed(string host, int port, ConnectionErrorCode code, Exception exception);
}
