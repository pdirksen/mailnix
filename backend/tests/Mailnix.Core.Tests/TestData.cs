using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using MimeKit;

namespace Mailnix.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static EvaluationContext Context(Func<EvaluationContext, EvaluationContext>? configure = null)
    {
        var context = new EvaluationContext
        {
            Now = Now,
            TimeZone = Berlin,
            MyAddresses = new HashSet<string> { "me@example.com" },
            SpecialFolders = new SpecialFolderTargets(Trash: "INBOX.Trash", Junk: "INBOX.Spam", Archive: "INBOX.Archive"),
            FolderDelimiter = '.',
        };
        return configure is null ? context : configure(context);
    }

    public static MessageSnapshot Fixture(string name, uint uid = 1, bool isSeen = false, IReadOnlyList<string>? keywords = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var message = MimeMessage.Load(path);
        return MessageSnapshotFactory.FromMimeMessage(message, uid, "INBOX", receivedAt: message.Date.AddMinutes(1), isSeen: isSeen, keywords: keywords);
    }

    public static MessageFacts Facts(string fixture, EvaluationContext? context = null) =>
        new(Fixture(fixture), context ?? Context());

    /// <summary>A minimal snapshot (envelope only, like a freshly fetched summary without headers).</summary>
    public static MessageSnapshot Simple(
        string from = "Alice <alice@example.org>",
        string subject = "Hello",
        string folder = "INBOX",
        bool isSeen = false,
        bool isFlagged = false,
        long size = 2048,
        DateTimeOffset? receivedAt = null,
        IReadOnlyList<string>? keywords = null)
    {
        var mailbox = MailboxAddress.Parse(from);
        return new MessageSnapshot
        {
            Uid = 7,
            Folder = folder,
            From = new EmailAddress(mailbox.Address, mailbox.Name),
            To = [new EmailAddress("me@example.com")],
            Subject = subject,
            ReceivedAt = receivedAt ?? Now.AddHours(-1),
            Size = size,
            IsSeen = isSeen,
            IsFlagged = isFlagged,
            Keywords = keywords ?? [],
        };
    }

    public static RuleDefinition Rule(
        ConditionNode conditions,
        IReadOnlyList<RuleAction>? actions = null,
        string name = "Rule",
        int priority = 0,
        Guid? id = null,
        bool enabled = true,
        bool stopProcessing = false,
        bool dryRun = false) => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = name,
            Priority = priority,
            Enabled = enabled,
            StopProcessing = stopProcessing,
            DryRun = dryRun,
            Conditions = conditions,
            Actions = actions ?? [],
        };

    public static Condition When(ConditionField field, ConditionOperator op, string? value = null, bool caseSensitive = false, string? headerName = null) =>
        new(field, op, value, headerName, caseSensitive);

    public static ConditionGroup All(params ConditionNode[] children) => new(GroupOperator.All, children);

    public static ConditionGroup Any(params ConditionNode[] children) => new(GroupOperator.Any, children);

    public static ConditionGroup None(params ConditionNode[] children) => new(GroupOperator.None, children);

    public static bool Matches(ConditionNode conditions, MessageSnapshot message, EvaluationContext? context = null)
    {
        context ??= Context();
        return RuleEvaluator.Evaluate(Rule(conditions), new MessageFacts(message, context), context).Matched;
    }
}
