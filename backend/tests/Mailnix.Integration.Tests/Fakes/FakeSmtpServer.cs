using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Mailnix.Integration.Tests.Fakes;

/// <summary>
/// A minimal SMTP server on a local port (no TLS): enough for MailKit to deliver a message. Optionally offers
/// AUTH PLAIN and checks the credentials.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly List<string> _messages = [];

    public FakeSmtpServer(string? username = null, string? password = null)
    {
        Username = username;
        Password = password;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? Username { get; }

    public string? Password { get; }

    /// <summary>The raw DATA of every delivered message.</summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake.smtp ready");
        try
        {
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-fake.smtp");
                        await writer.WriteLineAsync(Username is null ? "250 8BITMIME" : "250 AUTH PLAIN");
                        break;
                    case "AUTH":
                        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ')[2])).Split('\0');
                        await writer.WriteLineAsync(credentials[1] == Username && credentials[2] == Password ? "235 ok" : "535 Authentication failed");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                        {
                            data.Append(dataLine).Append('\n');
                        }

                        lock (_messages)
                        {
                            _messages.Add(data.ToString());
                        }

                        await writer.WriteLineAsync("250 queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 ok");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Client went away or the server stops.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
