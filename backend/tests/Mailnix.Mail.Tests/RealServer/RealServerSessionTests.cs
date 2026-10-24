using Mailnix.Core.Rules;
using Mailnix.Mail.Tests.Fakes;
using MimeKit;

namespace Mailnix.Mail.Tests.RealServer;

/// <summary>
/// Write tests against the real mailbox. Everything happens inside a fresh subfolder of INBOX.mailnix-test
/// (agreed with the mailbox owner) that is deleted afterwards; no other folder is touched.
/// </summary>
[Trait("Category", "RealServer")]
public sealed class RealServerSessionTests : IAsyncLifetime
{
    private static readonly ImapConnectionSettings? Settings = RealServerCredentials.Load();

    private IMailboxSession? _session;
    private string _root = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImapMailboxSessionFactory Factory() =>
        new(MailServices.Connector(MailServices.TestOptions(o => o.AllowInsecureConnections = false)));

    public async ValueTask InitializeAsync()
    {
        if (Settings is null)
        {
            return;
        }

        _session = await Factory().OpenAsync(Settings, Ct);
        var d = _session.Delimiter;
        _root = $"INBOX{d}mailnix-test{d}run-{Guid.NewGuid():N}"[..^24];
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
        {
            return;
        }

        await _session.DeleteFolderAsync(_root, CancellationToken.None);
        await _session.DisposeAsync();
    }

    private string Folder(string name) => $"{_root}{_session!.Delimiter}{name}";

    private static MimeMessage Message(string subject, string id, bool newsletter = false)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            MessageId = id,
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain") { Text = $"Test message {subject} from the mailnix test suite." },
        };
        message.From.Add(new MailboxAddress("mailnix test", "test@mailnix.invalid"));
        message.To.Add(new MailboxAddress("me", "me@mailnix.invalid"));
        if (newsletter)
        {
            message.Headers.Add("List-Unsubscribe", "<mailto:unsubscribe@mailnix.invalid>");
        }

        return message;
    }

    [Fact]
    public async Task Actions_and_undo_primitives_work_on_the_real_server()
    {
        Assert.SkipWhen(_session is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        var session = _session!;
        var source = Folder("source");

        var uid1 = await session.AppendAsync(source, Message("one", $"one-{Guid.NewGuid():N}@mailnix.invalid", newsletter: true), Ct);
        await session.AppendAsync(source, Message("two", $"two-{Guid.NewGuid():N}@mailnix.invalid"), Ct);
        await session.AppendAsync(source, Message("three", $"three-{Guid.NewGuid():N}@mailnix.invalid"), Ct);

        var status = await session.OpenAsync(source, writable: true, Ct);
        status.Count.ShouldBe(3);
        var all = await session.FetchNewAsync(source, 0, 10, FetchRequirements.Headers, Ct);
        all.Select(m => m.Subject).ShouldBe(["one", "two", "three"]);
        all[0].Uid.ShouldBe(uid1!.Value);
        all[0].HeaderValues("List-Unsubscribe").ShouldNotBeEmpty();
        (await session.FetchNewAsync(source, all[1].Uid, 10, FetchRequirements.None, Ct)).Single().Subject.ShouldBe("three");
        (await session.FetchLatestAsync(source, 2, FetchRequirements.None, Ct)).Select(m => m.Subject).ShouldBe(["three", "two"]);

        // Execute: flags/keyword on one, copy two, move two and three into a folder that does not exist yet.
        var rule = Guid.NewGuid();
        var copies = Folder("copies");
        var target = $"{Folder("target")}{session.Delimiter}nested";
        var outcomes = await session.ExecuteAsync(source,
        [
            new MessageActions(all[0], [new(rule, ActionType.MarkRead), new(rule, ActionType.Flag), new(rule, ActionType.AddKeyword, Keyword: "mailnix-test")]),
            new MessageActions(all[1], [new(rule, ActionType.CopyTo, copies), new(rule, ActionType.MoveTo, target)]),
            new MessageActions(all[2], [new(rule, ActionType.MoveTo, target)]),
        ], Ct);

        outcomes.ShouldAllBe(o => o.Success);
        outcomes.Count.ShouldBe(6);
        var moved = outcomes.Where(o => o.Action.Type == ActionType.MoveTo).ToList();
        moved.ShouldAllBe(o => o.TargetUid != null && o.TargetUidValidity != null);

        var left = await session.FetchLatestAsync(source, 10, FetchRequirements.None, Ct);
        left.Single().IsSeen.ShouldBeTrue();
        left.Single().IsFlagged.ShouldBeTrue();
        left.Single().Keywords.ShouldContain("mailnix-test");
        (await session.OpenAsync(target, writable: false, Ct)).Count.ShouldBe(2);
        (await session.OpenAsync(copies, writable: false, Ct)).Count.ShouldBe(1);

        // Undo primitives.
        var two = moved.Single(o => o.Uid == all[1].Uid);
        var located = await session.LocateAsync(target, two.TargetUidValidity!.Value, two.TargetUid!.Value, all[1].MessageId, Ct);
        located.ShouldBe(two.TargetUid);
        (await session.LocateAsync(target, 1, 999_999, all[1].MessageId, Ct)).ShouldBe(two.TargetUid); // stale UID: found by Message-ID
        (await session.MoveAsync(target, located!.Value, source, Ct)).ShouldNotBeNull();

        await session.SetFlagsAsync(source, all[0].Uid, seen: false, flagged: false, [], ["mailnix-test"], Ct);
        var restored = (await session.FetchAsync(source, [all[0].Uid], FetchRequirements.None, Ct)).Single();
        restored.IsSeen.ShouldBeFalse();
        restored.IsFlagged.ShouldBeFalse();
        restored.Keywords.ShouldNotContain("mailnix-test");

        var copy = outcomes.Single(o => o.Action.Type == ActionType.CopyTo);
        (await session.ExpungeAsync(copies, copy.TargetUid!.Value, Ct)).ShouldBeTrue();
        (await session.OpenAsync(copies, writable: false, Ct)).Count.ShouldBe(0);
        (await session.OpenAsync(source, writable: false, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Idle_wakes_up_when_a_message_arrives()
    {
        Assert.SkipWhen(_session is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        var watched = Folder("idle");
        await _session!.CreateFolderAsync(watched, Ct);
        await _session.OpenAsync(watched, writable: true, Ct);

        var idle = _session.IdleAsync(watched, TimeSpan.FromSeconds(30), CancellationToken.None, Ct);
        await Task.Delay(1000, Ct);
        await using (var other = await Factory().OpenAsync(Settings!, Ct))
        {
            await other.AppendAsync(watched, Message("arrives", $"idle-{Guid.NewGuid():N}@mailnix.invalid"), Ct);
        }

        (await idle.WaitAsync(TimeSpan.FromSeconds(25), Ct)).ShouldBe(IdleOutcome.NewMessages);
    }

    [Fact]
    public async Task Idle_can_be_woken_up_and_times_out()
    {
        Assert.SkipWhen(_session is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        var watched = Folder("idle2");
        await _session!.CreateFolderAsync(watched, Ct);

        using var wake = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        (await _session.IdleAsync(watched, TimeSpan.FromSeconds(30), wake.Token, Ct)).ShouldBe(IdleOutcome.Woken);
        (await _session.IdleAsync(watched, TimeSpan.FromSeconds(1), CancellationToken.None, Ct)).ShouldBe(IdleOutcome.TimedOut);
    }
}
