using Mailnix.Mail.Tests.Fakes;

namespace Mailnix.Mail.Tests;

public sealed class MailboxProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_capabilities_folders_and_counts()
    {
        await using var server = new FakeImapServer
        {
            Folders =
            [
                new("INBOX", @"\HasChildren", Messages: 12, Unread: 3),
                new("INBOX/Receipts", @"\HasNoChildren", Messages: 4),
                new("Archive", @"\Archive", Messages: 100),
                new("Deleted Items", @"\Trash"),
                new("Spam", @"\Junk"),
                new("Sent", @"\Sent"),
                new("Projects", @"\Noselect \HasChildren"),
            ],
        };

        var result = await MailServices.Probe().ProbeAsync(server.Settings(), includeCounts: true, Ct);

        result.Delimiter.ShouldBe('/');
        result.Capabilities.Move.ShouldBeTrue();
        result.Capabilities.Idle.ShouldBeTrue();
        result.Capabilities.QResync.ShouldBeFalse();
        result.SpecialFolders.ShouldBe(new SpecialFolderMap(Sent: "Sent", Drafts: null, Trash: "Deleted Items", Junk: "Spam", Archive: "Archive"));

        result.Folders[0].Path.ShouldBe("INBOX");
        result.Folders[0].SpecialUse.ShouldBe(SpecialFolderKind.Inbox);
        result.Folders[0].Messages.ShouldBe(12);
        result.Folders[0].Unread.ShouldBe(3);

        var receipts = result.Folders.Single(f => f.Path == "INBOX/Receipts");
        receipts.Name.ShouldBe("Receipts");
        receipts.ParentPath.ShouldBe("INBOX");
        receipts.Messages.ShouldBe(4);

        var projects = result.Folders.Single(f => f.Path == "Projects");
        projects.CanSelect.ShouldBeFalse();
        projects.Messages.ShouldBeNull();
    }

    [Fact]
    public async Task Falls_back_to_folder_names_without_special_use()
    {
        await using var server = new FakeImapServer
        {
            Capabilities = "IMAP4rev1",
            Delimiter = '.',
            Folders = [new("INBOX"), new("INBOX.Papierkorb"), new("INBOX.Gesendete Elemente"), new("INBOX.Entwürfe")],
        };

        var result = await MailServices.Probe().ProbeAsync(server.Settings(), includeCounts: false, Ct);

        result.Capabilities.SpecialUse.ShouldBeFalse();
        result.SpecialFolders.Trash.ShouldBe("INBOX.Papierkorb");
        result.SpecialFolders.Sent.ShouldBe("INBOX.Gesendete Elemente");
        result.SpecialFolders.Drafts.ShouldBe("INBOX.Entwürfe");
        result.Folders.ShouldAllBe(f => f.Messages == null);
    }
}
