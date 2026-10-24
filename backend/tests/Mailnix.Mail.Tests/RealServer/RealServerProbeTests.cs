using Mailnix.Mail.Tests.Fakes;

namespace Mailnix.Mail.Tests.RealServer;

/// <summary>
/// Read-only checks against a real mailbox (see <see cref="RealServerCredentials"/>); skipped when none is configured.
/// Never sends wrong credentials: repeated failed logins can get the test machine banned (fail2ban).
/// </summary>
[Trait("Category", "RealServer")]
public sealed class RealServerProbeTests
{
    private static readonly ImapConnectionSettings? Settings = RealServerCredentials.Load();

    [Fact]
    public async Task Probes_the_real_mailbox()
    {
        Assert.SkipWhen(Settings is null, "No real IMAP credentials configured (test-email-credentials.ini).");

        var result = await MailServices.Probe(MailServices.TestOptions(o => o.AllowInsecureConnections = false))
            .ProbeAsync(Settings!, includeCounts: true, TestContext.Current.CancellationToken);

        result.Folders.ShouldContain(f => f.SpecialUse == SpecialFolderKind.Inbox && f.Messages != null);
        result.Capabilities.All.ShouldNotBeEmpty();
        result.SpecialFolders.Trash.ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{result.Folders.Count} folders, delimiter '{result.Delimiter}', special: {result.SpecialFolders}");
    }
}
