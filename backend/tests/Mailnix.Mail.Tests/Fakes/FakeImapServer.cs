using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Mailnix.Mail.Tests.Fakes;

/// <summary>
/// Just enough of an IMAP server (plain text, LOGIN, LIST, STATUS) to exercise connection handling and
/// folder discovery deterministically, without Docker or a real mailbox.
/// </summary>
internal sealed partial class FakeImapServer : IAsyncDisposable
{
    public sealed record Folder(string Path, string Attributes = "", int Messages = 0, int Unread = 0);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public FakeImapServer()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Username { get; init; } = "user@example.com";

    public string Password { get; init; } = "secret";

    public char Delimiter { get; init; } = '/';

    public string Capabilities { get; init; } = "IMAP4rev1 SPECIAL-USE IDLE MOVE UIDPLUS";

    /// <summary>Accept connections but never send a greeting (to provoke timeouts).</summary>
    public bool Silent { get; init; }

    public IReadOnlyList<Folder> Folders { get; init; } = [new("INBOX")];

    public int LoginAttempts => _loginAttempts;

    private int _loginAttempts;
    private int _disposed;

    public ImapConnectionSettings Settings(string? password = null) =>
        new("127.0.0.1", Port, ImapSecurity.None, Username, password ?? Password);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = HandleAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

                if (Silent)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                }

                await writer.WriteLineAsync($"* OK [CAPABILITY {Capabilities}] Fake IMAP ready");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (!await RespondAsync(line, writer))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task<bool> RespondAsync(string line, StreamWriter writer)
    {
        var tokens = Tokenize(line);
        if (tokens.Count < 2)
        {
            await writer.WriteLineAsync("* BAD empty command");
            return true;
        }

        var (tag, command, args) = (tokens[0], tokens[1].ToUpperInvariant(), tokens.Skip(2).ToList());
        switch (command)
        {
            case "CAPABILITY":
                await writer.WriteLineAsync($"* CAPABILITY {Capabilities}");
                await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                break;
            case "LOGIN":
                Interlocked.Increment(ref _loginAttempts);
                await writer.WriteLineAsync(args.Count == 2 && args[0] == Username && args[1] == Password
                    ? $"{tag} OK [CAPABILITY {Capabilities}] Logged in"
                    : $"{tag} NO [AUTHENTICATIONFAILED] Authentication failed.");
                break;
            case "LIST":
                await ListAsync(tag, args, writer);
                break;
            case "STATUS":
                var folder = Folders.FirstOrDefault(f => f.Path == args[0]);
                if (folder is null)
                {
                    await writer.WriteLineAsync($"{tag} NO Mailbox doesn't exist");
                    break;
                }

                await writer.WriteLineAsync($"* STATUS {Quote(folder.Path)} (MESSAGES {folder.Messages} UNSEEN {folder.Unread})");
                await writer.WriteLineAsync($"{tag} OK STATUS completed");
                break;
            case "LOGOUT":
                await writer.WriteLineAsync("* BYE Logging out");
                await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                return false;
            case "NOOP" or "ID" or "ENABLE":
                await writer.WriteLineAsync($"{tag} OK {command} completed");
                break;
            default:
                await writer.WriteLineAsync($"{tag} BAD Unknown command {command}");
                break;
        }

        return true;
    }

    private async Task ListAsync(string tag, List<string> args, StreamWriter writer)
    {
        var pattern = args.Count > 0 ? args[^1] : "*";
        if (pattern.Length == 0)
        {
            // Hierarchy delimiter query.
            await writer.WriteLineAsync($"* LIST (\\Noselect) \"{Delimiter}\" \"\"");
        }
        else
        {
            foreach (var folder in Folders.Where(f => pattern == "*" || f.Path.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                await writer.WriteLineAsync($"* LIST ({folder.Attributes}) \"{Delimiter}\" {Quote(folder.Path)}");
            }
        }

        await writer.WriteLineAsync($"{tag} OK LIST completed");
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static List<string> Tokenize(string line) =>
        [.. TokenRegex().Matches(line).Select(m => m.Groups[1].Success ? Regex.Unescape(m.Groups[1].Value) : m.Groups[2].Value)];

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"|(\\([^)]*\\)|\\S+)")]
    private static partial Regex TokenRegex();

    /// <summary>Stops the server; safe to call more than once (tests stop it early to simulate an outage).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        _listener.Dispose();
        _stop.Dispose();
    }
}
