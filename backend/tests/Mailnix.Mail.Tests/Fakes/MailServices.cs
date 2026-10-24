using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailnix.Mail.Tests.Fakes;

internal static class MailServices
{
    /// <summary>Test defaults: plain-text connections allowed (the fake server has no TLS), short timeouts.</summary>
    public static MailOptions TestOptions(Action<MailOptions>? configure = null)
    {
        var options = new MailOptions
        {
            AllowInsecureConnections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            OperationTimeout = TimeSpan.FromSeconds(10),
        };
        configure?.Invoke(options);
        return options;
    }

    public static ImapConnector Connector(MailOptions? options = null) =>
        new(Options.Create(options ?? TestOptions()), NullLogger<ImapConnector>.Instance);

    public static MailboxProbe Probe(MailOptions? options = null) => new(Connector(options));
}
