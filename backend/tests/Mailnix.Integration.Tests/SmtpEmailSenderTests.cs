using System.Net;
using System.Net.Sockets;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail.Email;
using Microsoft.Extensions.Options;

namespace Mailnix.Integration.Tests;

/// <summary>The real MailKit path against a small SMTP server on localhost.</summary>
public sealed class SmtpEmailSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SmtpEmailSender Sender(int port, string? username = null, string? password = null) => new(Options.Create(new EmailOptions
    {
        Host = "127.0.0.1",
        Port = port,
        Security = SmtpSecurity.None,
        Username = username,
        Password = password,
        FromAddress = "mailnix@example.org",
        FromName = "mailnix",
        Timeout = TimeSpan.FromSeconds(5),
    }));

    [Fact]
    public async Task Delivers_text_and_html_from_the_configured_sender()
    {
        await using var server = new FakeSmtpServer(username: "relay", password: "secret");

        await Sender(server.Port, "relay", "secret").SendAsync(new EmailMessage("pat@example.com", "Hello", "Plain text", "<p>HTML</p>"), Ct);

        var message = server.Messages.ShouldHaveSingleItem();
        message.ShouldContain("From: mailnix <mailnix@example.org>");
        message.ShouldContain("To: pat@example.com");
        message.ShouldContain("Subject: Hello");
        message.ShouldContain("Plain text");
        message.ShouldContain("<p>HTML</p>");
    }

    [Fact]
    public async Task Failures_become_email_exceptions_with_the_reason()
    {
        await using var server = new FakeSmtpServer(username: "relay", password: "secret");
        var wrong = await Should.ThrowAsync<EmailException>(() => Sender(server.Port, "relay", "wrong").SendAsync(new EmailMessage("pat@example.com", "Hi", "Text"), Ct));
        wrong.Message.ShouldContain("Authentication failed");

        // A username for a server that offers no sign-in.
        await using var open = new FakeSmtpServer();
        await Should.ThrowAsync<EmailException>(() => Sender(open.Port, "relay", "secret").SendAsync(new EmailMessage("pat@example.com", "Hi", "Text"), Ct));

        // Nobody listening.
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        await Should.ThrowAsync<EmailException>(() => Sender(port).SendAsync(new EmailMessage("pat@example.com", "Hi", "Text"), Ct));
    }

    [Fact]
    public async Task Without_server_settings_nothing_is_sent()
    {
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions()));
        sender.IsConfigured.ShouldBeFalse();
        await Should.ThrowAsync<EmailException>(() => sender.SendAsync(new EmailMessage("pat@example.com", "Hi", "Text"), Ct));
    }
}
