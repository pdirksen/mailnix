using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailnix.Mail.Email;

/// <param name="Html">Optional HTML alternative to <paramref name="Text"/>.</param>
public sealed record EmailMessage(string To, string Subject, string Text, string? Html = null);

public interface IEmailSender
{
    /// <summary>Whether an SMTP server is configured; without one, nothing is sent.</summary>
    bool IsConfigured { get; }

    /// <exception cref="EmailException">The server could not be reached or refused the message.</exception>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends through the configured SMTP server with MailKit, one connection per message.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private EmailOptions Options => options.Value;

    public bool IsConfigured => Options.IsConfigured;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Options is not { IsConfigured: true, Host: { } host, FromAddress: { } from })
        {
            throw new EmailException("No SMTP server is configured.");
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(Options.FromName, from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html };
        mime.Body = body.ToMessageBody();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.Timeout);
        using var client = new SmtpClient { Timeout = (int)Options.Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(host, Options.EffectivePort, ToSocketOptions(Options.Security), timeout.Token);
            if (!string.IsNullOrEmpty(Options.Username))
            {
                await client.AuthenticateAsync(Options.Username, Options.Password ?? "", timeout.Token);
            }

            await client.SendAsync(mime, timeout.Token);
            await client.DisconnectAsync(quit: true, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmailException($"The SMTP server {Options.Host} did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or MailKit.ProtocolException
            or MailKit.CommandException or AuthenticationException or SslHandshakeException or NotSupportedException)
        {
            throw new EmailException($"Sending through {Options.Host} failed: {ex.Message}", ex);
        }
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls,
    };
}
