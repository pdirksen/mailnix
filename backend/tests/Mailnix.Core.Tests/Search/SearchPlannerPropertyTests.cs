using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests.Search;

/// <summary>
/// The search must never drop a message the rules match: for random rules and messages, every local match is
/// among the server's search results (modelled by <see cref="ImapSearchModel"/>).
/// </summary>
public sealed class SearchPlannerPropertyTests
{
    private static readonly string[] Addresses =
        ["alice@example.org", "bob@shop.example.com", "news@news.shop.example.com", "me@example.com", "noreply@bank.test", "me+tag@example.com"];

    private static readonly string?[] Names = [null, "Alice", "Shop News", "Bank", "bob@shop.example.com"];
    private static readonly string[] Subjects = ["Hello", "Your invoice 42", "Weekly deals", "Re: Invoice", "SALE now", ""];
    private static readonly string?[] ListIds = [null, "Shop News <news.shop.example.com>"];
    private static readonly string[] Keywords = ["$label1", "work", "Todo"];
    private static readonly long[] Sizes = [100, 2048, 2049, 50_000];
    private static readonly double[] AgesInDays = [0.2, 0.9, 1.5, 3, 10, 29.9, 30.1, 40, 400];

    private static readonly string[] TextOperands =
    [
        "alice", "ALICE@example.org", "example.org", "shop.example.com", "Shop.Example.COM", "news", "invoice", "Invoice 42",
        "deals", "bank", "me@example.com", "me+tag", "tag", "Alice", "Shop News", "sale", "news.shop.example.com", "alpha", "x",
        "@example.com", "example", ".com", "re:", "",
    ];

    private static readonly HashSet<ConditionField> Searchable =
    [
        ConditionField.From, ConditionField.FromDomain, ConditionField.FromName, ConditionField.To, ConditionField.Cc,
        ConditionField.Recipients, ConditionField.ReplyTo, ConditionField.Subject, ConditionField.Header, ConditionField.ListId,
        ConditionField.IsUnread, ConditionField.IsFlagged, ConditionField.IsAnswered, ConditionField.Keyword,
        ConditionField.Size, ConditionField.AgeDays, ConditionField.Date,
    ];

    private static readonly NamedList AddressList = new(Guid.NewGuid(), "VIP", NamedListKind.Address, ["alice@example.org", "noreply@bank.test"]);
    private static readonly NamedList DomainList = new(Guid.NewGuid(), "Shops", NamedListKind.Domain, ["shop.example.com", "example.org"]);
    private static readonly NamedList PatternList = new(Guid.NewGuid(), "Patterns", NamedListKind.Pattern, ["*@*.example.com"]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Search_results_contain_every_local_match(int seed)
    {
        var random = new Random(seed);
        var messages = Enumerable.Range(1, 80).Select(i => RandomMessage(random, (uint)i)).ToList();
        int pushedDown = 0, matches = 0, excluded = 0;

        for (var round = 0; round < 1000; round++)
        {
            // A few rules other rules can reference, then the rules of the run.
            var referenced = Enumerable.Range(0, 3).Select(i => Rule(RandomNode(random, depth: 2, []), name: $"Ref {i}")).ToList();
            var context = Context(c => c with
            {
                NamedLists = new Dictionary<Guid, NamedList> { [AddressList.Id] = AddressList, [DomainList.Id] = DomainList, [PatternList.Id] = PatternList },
                Rules = referenced.ToDictionary(r => r.Id),
            });
            var rules = Enumerable.Range(0, random.Next(1, 3))
                .Select(_ => Rule(RandomNode(random, depth: 3, referenced), [new FlagAction()]))
                .ToList();

            var filter = SearchPlanner.Plan(rules, context);
            pushedDown += filter is null ? 0 : 1;
            foreach (var message in messages)
            {
                var facts = new MessageFacts(message, context);
                var matched = RuleEvaluator.EvaluateAll(rules, facts, context).Any(e => e.Matched);
                matches += matched ? 1 : 0;
                excluded += ImapSearchModel.Matches(filter, message) ? 0 : 1;
                if (matched && !ImapSearchModel.Matches(filter, message))
                {
                    Assert.Fail($"Seed {seed}, round {round}: message {message.Uid} ({message.From}, \"{message.Subject}\") matches locally but not {filter!.Describe()}.");
                }
            }
        }

        // The generator must actually exercise the planner: searches are built and narrow things down.
        pushedDown.ShouldBeGreaterThan(150);
        matches.ShouldBeGreaterThan(1000);
        excluded.ShouldBeGreaterThan(6000);
    }

    private static MessageSnapshot RandomMessage(Random random, uint uid)
    {
        T Pick<T>(IReadOnlyList<T> values) => values[random.Next(values.Count)];
        var from = new EmailAddress(Pick(Addresses), Pick(Names));
        var to = Enumerable.Range(0, random.Next(0, 3)).Select(_ => new EmailAddress(Pick(Addresses))).ToList();
        var cc = Enumerable.Range(0, random.Next(0, 2)).Select(_ => new EmailAddress(Pick(Addresses), Pick(Names))).ToList();
        var replyTo = random.Next(3) == 0 ? [new EmailAddress(Pick(Addresses))] : new List<EmailAddress>();
        var subject = Pick(Subjects);
        var listId = Pick(ListIds);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("From", from.ToString()),
            new("To", string.Join(", ", to)),
            new("Subject", subject),
        };
        if (cc.Count > 0)
        {
            headers.Add(new("Cc", string.Join(", ", cc)));
        }

        if (replyTo.Count > 0)
        {
            headers.Add(new("Reply-To", replyTo[0].ToString()));
        }

        if (listId is not null)
        {
            headers.Add(new("List-Id", listId));
        }

        if (random.Next(2) == 0)
        {
            headers.Add(new("X-Custom", Pick(["alpha", "beta", "Alpha Beta"])));
        }

        return new MessageSnapshot
        {
            Uid = uid,
            From = from,
            To = to,
            Cc = cc,
            ReplyTo = replyTo,
            Subject = subject,
            ReceivedAt = Now.AddDays(-Pick(AgesInDays)),
            Size = Pick(Sizes),
            IsSeen = random.Next(2) == 0,
            IsFlagged = random.Next(3) == 0,
            IsAnswered = random.Next(4) == 0,
            Keywords = [.. Keywords.Where(_ => random.Next(3) == 0)],
            Headers = headers,
            Attachments = [],
            BodyText = Pick(["", "Click to unsubscribe", "Invoice attached"]),
        };
    }

    private static ConditionNode RandomNode(Random random, int depth, IReadOnlyList<RuleDefinition> referencable)
    {
        if (depth > 0 && random.Next(3) == 0)
        {
            var op = (GroupOperator)random.Next(3);
            var children = Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomNode(random, depth - 1, referencable)).ToArray();
            return new ConditionGroup(op, children);
        }

        if (referencable.Count > 0 && random.Next(8) == 0)
        {
            var target = referencable[random.Next(referencable.Count)];
            return When(ConditionField.Rule, random.Next(2) == 0 ? ConditionOperator.Matches : ConditionOperator.NotMatches, target.Id.ToString());
        }

        return RandomCondition(random);
    }

    private static Condition RandomCondition(Random random)
    {
        T Pick<T>(IReadOnlyList<T> values) => values[random.Next(values.Count)];
        // Mostly fields the server can search, so that searches are actually built; the rest stays in the mix.
        var definition = random.Next(4) == 0
            ? Pick(FieldCatalog.All.Where(d => d.Field != ConditionField.Rule).ToList())
            : Pick(FieldCatalog.All.Where(d => Searchable.Contains(d.Field)).ToList());
        var op = Pick(definition.Operators);
        var headerName = definition.RequiresHeaderName ? Pick(["X-Custom", "List-Id", "Reply-To", "x-custom"]) : null;

        string? value = op switch
        {
            _ when FieldCatalog.IsUnary(op) => null,
            ConditionOperator.InList or ConditionOperator.NotInList => Pick([AddressList.Id, DomainList.Id, PatternList.Id]).ToString(),
            ConditionOperator.Regex => Pick(["^re:", "invoice \\d+", "[", "shop"]),
            _ => definition.ValueType switch
            {
                FieldValueType.Number when definition.Field == ConditionField.AgeDays => Pick(["0.5", "1", "2", "9", "30", "365"]),
                FieldValueType.Number => Pick(["0", "1", "100", "2048", "2048.5", "2049", "50000"]),
                FieldValueType.Date => Pick(["2026-10-20", "2026-10-23", "2026-10-24", "2025-01-01", "2026-09-24"]),
                FieldValueType.Enum => Pick(definition.Options!),
                _ when definition.Field == ConditionField.Keyword => Pick(["$label1", "WORK", "todo", "none"]),
                _ => Pick(TextOperands),
            },
        };

        return When(definition.Field, op, value, caseSensitive: random.Next(4) == 0, headerName: headerName);
    }
}
