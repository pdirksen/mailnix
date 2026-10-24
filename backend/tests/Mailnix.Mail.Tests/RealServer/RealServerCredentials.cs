namespace Mailnix.Mail.Tests.RealServer;

/// <summary>
/// Credentials for an optional real IMAP account, read from an INI file that is never committed:
/// the path in MAILNIX_TEST_IMAP_CREDENTIALS, or test-email-credentials.ini in the repository root.
/// <code>
/// [imap]
/// host=imap.example.com
/// port=993
/// security=SSL        ; SSL | STARTTLS | NONE
/// user=me@example.com
/// password=...
/// </code>
/// </summary>
internal static class RealServerCredentials
{
    private const string FileName = "test-email-credentials.ini";

    public static ImapConnectionSettings? Load()
    {
        var path = Environment.GetEnvironmentVariable("MAILNIX_TEST_IMAP_CREDENTIALS") ?? FindUpwards(FileName);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        var values = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] is not ('#' or ';' or '[') && line.Contains('=', StringComparison.Ordinal))
            .ToDictionary(line => line[..line.IndexOf('=', StringComparison.Ordinal)].Trim().ToUpperInvariant(), line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..]);

        var security = values.GetValueOrDefault("SECURITY", "SSL").Trim().ToUpperInvariant() switch
        {
            "SSL" or "TLS" => ImapSecurity.SslOnConnect,
            "STARTTLS" => ImapSecurity.StartTls,
            _ => ImapSecurity.None,
        };
        var port = int.TryParse(values.GetValueOrDefault("PORT"), out var p) ? p : ImapConnectionSettings.DefaultPort(security);

        return new ImapConnectionSettings(values["HOST"].Trim(), port, security, values["USER"].Trim(), values["PASSWORD"]);
    }

    private static string? FindUpwards(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
