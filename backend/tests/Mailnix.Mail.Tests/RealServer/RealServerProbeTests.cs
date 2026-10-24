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

[Trait("Category", "RealServer")]
public sealed class RealServerReaderTests
{
    private static readonly ImapConnectionSettings? Settings = RealServerCredentials.Load();

    [Fact]
    public async Task Reading_messages_changes_nothing_on_the_server()
    {
        Assert.SkipWhen(Settings is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        var ct = TestContext.Current.CancellationToken;
        var options = MailServices.TestOptions(o => o.AllowInsecureConnections = false);
        var probe = MailServices.Probe(options);
        int? Unread(MailboxProbeResult r) => r.Folders.Single(f => f.SpecialUse == SpecialFolderKind.Inbox).Unread;

        var before = Unread(await probe.ProbeAsync(Settings!, includeCounts: true, ct));
        var sample = await new MessageReader(MailServices.Connector(options)).ReadLatestAsync(
            Settings!, "INBOX", 10, Core.Rules.FetchRequirements.Headers | Core.Rules.FetchRequirements.Body, ct);
        var after = Unread(await probe.ProbeAsync(Settings!, includeCounts: true, ct));

        sample.Messages.Count.ShouldBe(Math.Min(10, sample.TotalMessages));
        sample.Messages.ShouldAllBe(m => m.Headers != null && m.From != null);
        sample.Messages.Select(m => m.Uid).ShouldBeInOrder(SortDirection.Descending);
        after.ShouldBe(before); // opened read-only, bodies fetched with PEEK
    }
}
