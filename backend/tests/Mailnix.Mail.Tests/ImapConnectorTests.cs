using System.Net;
using System.Net.Sockets;
using Mailnix.Mail.Tests.Fakes;

namespace Mailnix.Mail.Tests;

public sealed class ImapConnectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connects_and_authenticates()
    {
        await using var server = new FakeImapServer();

        using var client = await MailServices.Connector().ConnectAsync(server.Settings(), Ct);

        client.IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_authentication_failure()
    {
        await using var server = new FakeImapServer();

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector().ConnectAsync(server.Settings(password: "wrong"), Ct));

        error.Code.ShouldBe(ConnectionErrorCode.AuthenticationFailed);
        error.Message.ShouldNotContain("wrong");
        server.LoginAttempts.ShouldBe(1); // never retried: retries could trip fail2ban on real servers
    }

    [Fact]
    public async Task Silent_server_times_out()
    {
        await using var server = new FakeImapServer { Silent = true };
        var options = MailServices.TestOptions(o => o.ConnectTimeout = TimeSpan.FromMilliseconds(500));

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector(options).ConnectAsync(server.Settings(), Ct));

        error.Code.ShouldBe(ConnectionErrorCode.Timeout);
    }

    [Fact]
    public async Task Closed_port_is_reported_as_connection_refused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector().ConnectAsync(new("127.0.0.1", port, ImapSecurity.None, "u", "p"), Ct));

        error.Code.ShouldBe(ConnectionErrorCode.ConnectionRefused);
    }

    [Fact]
    public async Task Unknown_host_is_reported()
    {
        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector().ConnectAsync(new("does-not-exist.invalid", 993, ImapSecurity.SslOnConnect, "u", "p"), Ct));

        error.Code.ShouldBe(ConnectionErrorCode.HostNotFound);
    }

    [Fact]
    public async Task Tls_against_a_plain_text_server_fails_with_tls_error()
    {
        await using var server = new FakeImapServer();

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector().ConnectAsync(server.Settings() with { Security = ImapSecurity.SslOnConnect }, Ct));

        error.Code.ShouldBe(ConnectionErrorCode.TlsFailed);
    }

    [Fact]
    public async Task Starttls_against_a_server_without_it_fails_with_tls_error()
    {
        await using var server = new FakeImapServer();

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector().ConnectAsync(server.Settings() with { Security = ImapSecurity.StartTls }, Ct));

        error.Code.ShouldBe(ConnectionErrorCode.TlsFailed);
    }

    [Fact]
    public async Task Unencrypted_connections_are_refused_unless_enabled()
    {
        await using var server = new FakeImapServer();
        var options = MailServices.TestOptions(o => o.AllowInsecureConnections = false);

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector(options).ConnectAsync(server.Settings(), Ct));

        error.Code.ShouldBe(ConnectionErrorCode.InsecureConnectionNotAllowed);
        server.LoginAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task Private_addresses_are_refused_when_blocked()
    {
        await using var server = new FakeImapServer();
        var options = MailServices.TestOptions(o => o.BlockPrivateNetworks = true);

        var error = await Should.ThrowAsync<MailConnectionException>(
            () => MailServices.Connector(options).ConnectAsync(server.Settings() with { Host = "localhost" }, Ct));

        error.Code.ShouldBe(ConnectionErrorCode.AddressNotAllowed);
        server.LoginAttempts.ShouldBe(0);
    }

    [Fact]
    public void Settings_never_print_the_password()
    {
        var settings = new ImapConnectionSettings("imap.example.com", 993, ImapSecurity.SslOnConnect, "me", "hunter2-secret");

        settings.ToString().ShouldNotContain("hunter2-secret");
        settings.ToString().ShouldContain("imap.example.com");
    }
}
