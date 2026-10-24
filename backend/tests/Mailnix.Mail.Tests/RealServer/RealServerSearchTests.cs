using Mailnix.Core.Rules;
using Mailnix.Core.Tests.Search;
using Mailnix.Mail.Tests.Fakes;
using MimeKit;

namespace Mailnix.Mail.Tests.RealServer;

/// <summary>
/// The real server's SEARCH behaves like <see cref="ImapSearchModel"/>, which the planner's property tests rely on.
/// Works only inside a fresh subfolder of INBOX.mailnix-test, deleted afterwards.
/// </summary>
[Trait("Category", "RealServer")]
public sealed class RealServerSearchTests : IAsyncLifetime
{
    private static readonly ImapConnectionSettings? Settings = RealServerCredentials.Load();

    private IMailboxSession? _session;
    private string _root = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (Settings is null)
        {
            return;
        }

        _session = await new ImapMailboxSessionFactory(MailServices.Connector(MailServices.TestOptions(o => o.AllowInsecureConnections = false))).OpenAsync(Settings, Ct);
        var d = _session.Delimiter;
        _root = $"INBOX{d}mailnix-test{d}search-{Guid.NewGuid().ToString("N")[..8]}";
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

    private static MimeMessage Message(string fromName, string from, string subject, string to = "me@mailnix.invalid", string? listId = null, string? custom = null, int padding = 0)
    {
        var message = new MimeMessage
        {
            MessageId = $"{Guid.NewGuid():N}@mailnix.invalid",
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain") { Text = "mailnix search test " + new string('x', padding) },
        };
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(MailboxAddress.Parse(to));
        // Set raw so encoded words reach the server as written (servers must decode them for SUBJECT searches).
        message.Headers.Add(new Header(HeaderId.Subject, subject));
        if (listId is not null)
        {
            message.Headers.Add("List-Id", listId);
        }

        if (custom is not null)
        {
            message.Headers.Add("X-Custom", custom);
        }

        return message;
    }

    [Fact]
    public async Task Server_search_matches_the_reference_model()
    {
        Assert.SkipWhen(_session is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        var session = _session!;
        var folder = _root;

        await session.AppendAsync(folder, Message("Shop News", "news@shop.mailnix.invalid", "Weekly deals", listId: "Shop News <news.shop.mailnix.invalid>"), Ct);
        await session.AppendAsync(folder, Message("Billing", "billing@shop.mailnix.invalid", "=?utf-8?q?Your_invoice_99?=", padding: 5000), Ct);
        await session.AppendAsync(folder, Message("Alice", "alice@mailnix.invalid", "Re: Invoice", to: "Team <team@mailnix.invalid>", custom: "alpha"), Ct);
        await session.AppendAsync(folder, Message("Müller Shop", "info@mueller.mailnix.invalid", "Hello", custom: "Alpha Beta"), Ct);
        await session.AppendAsync(folder, Message("Bob", "bob@mailnix.invalid", "SALE now", padding: 20000), Ct);

        var all = await session.FetchAsync(folder, (await session.SearchAsync(folder, null, Ct)).Uids, FetchRequirements.Headers, Ct);
        all.Count.ShouldBe(5);
        var byUid = all.OrderBy(m => m.Uid).ToList();
        await session.SetFlagsAsync(folder, byUid[0].Uid, seen: true, flagged: null, [], [], Ct);
        await session.SetFlagsAsync(folder, byUid[1].Uid, seen: null, flagged: true, ["$label1"], [], Ct);
        await session.SetFlagsAsync(folder, byUid[2].Uid, seen: true, flagged: true, [], [], Ct);
        all = await session.FetchAsync(folder, [.. all.Select(m => m.Uid)], FetchRequirements.Headers, Ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sizes = all.Select(m => m.Size!.Value).Order().ToList();
        SearchFilter[] filters =
        [
            new SearchText(SearchTextField.From, "shop"),
            new SearchText(SearchTextField.From, "SHOP.mailnix.invalid"),
            new SearchText(SearchTextField.From, "ller Shop"),
            new SearchText(SearchTextField.To, "team@"),
            new SearchText(SearchTextField.Cc, "anything"),
            new SearchText(SearchTextField.Subject, "invoice"),
            new SearchText(SearchTextField.Subject, "INVOICE 99"),
            new SearchText(SearchTextField.Subject, "re: inv"),
            new SearchText(SearchTextField.Header, string.Empty, "List-Id"),
            new SearchText(SearchTextField.Header, "news.shop", "List-Id"),
            new SearchText(SearchTextField.Header, "alpha", "X-Custom"),
            new SearchFlagIs(SearchFlagKind.Seen, false),
            new SearchFlagIs(SearchFlagKind.Flagged, true),
            new SearchFlagIs(SearchFlagKind.Answered, false),
            new SearchKeyword("$label1"),
            new SearchLarger(sizes[2]),
            new SearchSmaller(sizes[2]),
            new SearchAnd([new SearchLarger(sizes[0] - 1), new SearchSmaller(sizes[0] + 1)]),
            new SearchBefore(today.AddDays(5)),
            new SearchBefore(today.AddDays(-5)),
            new SearchSince(today.AddDays(-5)),
            new SearchSince(today.AddDays(5)),
            new SearchNot(new SearchFlagIs(SearchFlagKind.Flagged, true)),
            new SearchNot(new SearchOr([new SearchFlagIs(SearchFlagKind.Seen, true), new SearchKeyword("$label1")])),
            new SearchOr([new SearchText(SearchTextField.Subject, "deals"), new SearchText(SearchTextField.From, "bob")]),
            new SearchAnd([new SearchText(SearchTextField.From, "shop"), new SearchFlagIs(SearchFlagKind.Seen, false)]),
        ];

        foreach (var filter in filters)
        {
            var server = (await session.SearchAsync(folder, filter, Ct)).Uids;
            var model = all.Where(m => ImapSearchModel.Matches(filter, m)).Select(m => m.Uid).Order().ToList();
            server.ShouldBe(model, $"{filter.Describe()}: server {string.Join(",", server)}, model {string.Join(",", model)}");
        }
    }

    [Fact]
    public async Task Purge_removes_exactly_the_given_messages()
    {
        Assert.SkipWhen(_session is null, "No real IMAP credentials configured (test-email-credentials.ini).");
        var session = _session!;
        var folder = _root;
        foreach (var subject in new[] { "one", "two", "three" })
        {
            await session.AppendAsync(folder, Message("Test", "test@mailnix.invalid", subject), Ct);
        }

        var uids = (await session.SearchAsync(folder, null, Ct)).Uids;
        (await session.PurgeAsync(folder, [uids[0], uids[2]], Ct)).ShouldBeTrue();

        var left = await session.FetchAsync(folder, (await session.SearchAsync(folder, null, Ct)).Uids, FetchRequirements.None, Ct);
        left.Single().Subject.ShouldBe("two");
    }
}
