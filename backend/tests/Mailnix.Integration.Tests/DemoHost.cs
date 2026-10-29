using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mailnix.Api.Hosting;
using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mailnix.Integration.Tests;

/// <summary>
/// Not a test: mailnix with a made-up mailbox on http://localhost:5090, for the front page screenshots
/// (docs/frontpage/README.md). It serves the SPA from Mailnix.Api/wwwroot, signs up <see cref="Email"/>, sorts some
/// mail with a few rules, adds 30 days of statistics and then waits (MAILNIX_DEMO_MINUTES, default 30).
/// Never touches a real mail server.
/// </summary>
public sealed class DemoHost
{
    public const string Email = "alex@example.com";
    public const string Password = "demo password 123";
    private const int Port = 5090;

    /// <summary>Exists while the demo is ready (the screenshot script waits for it).</summary>
    private static readonly string ReadyFile = Path.Combine(Path.GetTempPath(), "mailnix-demo.ready");

    private static readonly string[] Folders = ["INBOX", "Archive", "Drafts", "Sent", "Trash", "Junk", "Newsletters", "Finance", "Shops"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Explicit = true)]
    public async Task Run_the_demo_instance()
    {
        File.Delete(ReadyFile);
        var mail = new FakeMailServer { Delimiter = '/' };
        foreach (var folder in Folders)
        {
            mail.CreateFolder(folder);
        }

        await using var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.500")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:02")
            .WithServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(mail));
                s.Replace(ServiceDescriptor.Singleton<IMailboxProbe>(new Probe(mail)));
                s.Replace(ServiceDescriptor.Singleton<IMessageReader>(new Reader(mail)));
            });
        app.UseKestrel(Port);
        app.StartServer();

        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://localhost:{Port}") };
        client.DefaultRequestHeaders.Add(CsrfHeaderMiddleware.HeaderName, CsrfHeaderMiddleware.HeaderValue);
        await PostAsync(client, "/api/auth/register", new { email = Email, password = Password, displayName = "Alex Morgan", timeZone = "Europe/Berlin", language = "en" });

        var now = DateTimeOffset.UtcNow;
        foreach (var message in ArchiveMail(now))
        {
            mail.Append("Archive", message);
        }

        foreach (var message in InboxMail(now))
        {
            mail.Append("INBOX", message);
        }

        var vip = await IdOfAsync(PostAsync(client, "/api/lists", new
        {
            name = "VIP senders",
            kind = "Domain",
            description = "Never sorted away",
            entries = new[] { new { value = "northwind.example", note = "Northwind" }, new { value = "fabrikam.example", note = "Fabrikam legal" } },
        }));
        var account = await IdOfAsync(PostAsync(client, "/api/accounts", new
        {
            name = "Work",
            host = "imap.example.com",
            port = 993,
            security = "SslOnConnect",
            username = Email,
            password = "not a real password",
            watchedFolders = new[] { "INBOX" },
            specialFolders = new { sent = "Sent", drafts = "Drafts", trash = "Trash", junk = "Junk", archive = "Archive" },
        }));
        await EventuallyAsync(app, db => db.FolderStates.AnyAsync(f => f.AccountId == account, Ct));

        var rules = new List<Guid>();
        async Task<Guid> RuleAsync(object rule) => await IdOfAsync(PostAsync(client, "/api/rules", rule));
        rules.Add(await RuleAsync(new
        {
            accountId = account,
            name = "VIP senders",
            description = "Flag mail from the people that matter.",
            conditions = Condition("FromDomain", "InList", vip.ToString()),
            actions = new object[] { new { type = "Flag" }, new { type = "AddKeyword", keyword = "vip" } },
        }));
        rules.Add(await RuleAsync(new
        {
            accountId = account,
            name = "Invoices and receipts",
            conditions = Group("All", Group("Any", Condition("Subject", "Contains", "invoice"), Condition("Subject", "Contains", "receipt")), Condition("HasAttachment", "IsTrue")),
            actions = new object[] { new { type = "AddKeyword", keyword = "invoice" }, new { type = "MoveTo", folder = "Finance/Invoices/{year}" } },
        }));
        rules.Add(await RuleAsync(new
        {
            accountId = account,
            name = "Shop orders by plus address",
            conditions = Condition("PlusTag", "Exists"),
            actions = new object[] { new { type = "MoveTo", folder = "Shops/{plusTag}" } },
        }));
        rules.Add(await RuleAsync(new
        {
            accountId = account,
            name = "Newsletters",
            description = "Bulk mail goes to a folder per year, unless it is from a VIP.",
            conditions = Group("All", Condition("IsNewsletter", "IsTrue"), Condition("FromDomain", "NotInList", vip.ToString())),
            actions = new object[] { new { type = "MarkRead" }, new { type = "MoveTo", folder = "Newsletters/{year}" } },
        }));
        rules.Add(await RuleAsync(new
        {
            accountId = account,
            name = "Auto-replies",
            conditions = Condition("IsAutoReply", "IsTrue"),
            actions = new object[] { new { type = "MarkRead" }, new { type = "MoveTo", folder = "Archive" } },
        }));
        rules.Add(await RuleAsync(new
        {
            accountId = account,
            name = "Old newsletters to Trash",
            conditions = Condition("AgeDays", "GreaterThan", "60"),
            actions = new object[] { new { type = "Delete" } },
            dryRun = true,
            trigger = "Scheduled",
            schedule = "0 3 * * *",
            sourceFolders = new[] { "Newsletters" },
        }));

        // Existing mail, sorted with "Run now".
        var run = await IdOfAsync(PostAsync(client, $"/api/accounts/{account}/runs", new { folder = "INBOX", limit = 100 }));
        await EventuallyAsync(app, db => db.Runs.AnyAsync(r => r.Id == run && r.State == RunState.Completed, Ct));

        // New mail, one message at a time: each one is a run of its own.
        foreach (var message in NewMail())
        {
            mail.Append("INBOX", message with { Date = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow });
            await Task.Delay(2500, Ct);
        }

        await EventuallyAsync(app, async db => await db.Runs.CountAsync(r => r.Trigger == RunTrigger.NewMail, Ct) >= 3);
        var autoReply = await DbAsync(app, db => db.Runs.Where(r => r.Trigger == RunTrigger.NewMail).OrderBy(r => r.CreatedAt).Select(r => r.Id).FirstAsync(Ct));
        await PostAsync(client, $"/api/runs/{autoReply}/undo", new { });
        await EventuallyAsync(app, db => db.Runs.AnyAsync(r => r.Trigger == RunTrigger.Undo && r.State == RunState.Completed, Ct));

        await SeedHistoryAsync(app, account, rules);

        await File.WriteAllTextAsync(ReadyFile, $"http://localhost:{Port}", Ct);
        try
        {
            var minutes = int.TryParse(Environment.GetEnvironmentVariable("MAILNIX_DEMO_MINUTES"), out var m) ? m : 30;
            await Task.Delay(TimeSpan.FromMinutes(minutes), Ct);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        finally
        {
            File.Delete(ReadyFile);
        }
    }

    private static object Condition(string field, string op, string? value = null) => new { kind = "condition", field, @operator = op, value };

    private static object Group(string op, params object[] children) => new { kind = "group", @operator = op, children };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body, Ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        }

        return response;
    }

    private static async Task<Guid> IdOfAsync(Task<HttpResponseMessage> request) =>
        (await (await request).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

    private static async Task<T> DbAsync<T>(MailnixAppFactory app, Func<MailnixDbContext, Task<T>> query)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<MailnixDbContext>());
    }

    private static async Task EventuallyAsync(MailnixAppFactory app, Func<MailnixDbContext, Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!await DbAsync(app, condition))
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The demo did not get there in time.");
            }

            await Task.Delay(200, Ct);
        }
    }

    /// <summary>
    /// Daily counters for the 29 days before today, and half a day on top of today's real ones, so the dashboard and
    /// stats have a history.
    /// </summary>
    private static async Task SeedHistoryAsync(MailnixAppFactory app, Guid accountId, List<Guid> rules)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var account = await db.MailAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == accountId, Ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).DateTime);
        var rows = await db.StatsDaily.Where(s => s.AccountId == account.Id).ToListAsync(Ct);
        var random = new Random(4711);
        var (vip, invoices, shops, newsletters, autoReplies) = (rules[0], rules[1], rules[2], rules[3], rules[4]);

        void Add(DateOnly date, Guid rule, string metric, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (rows.Find(s => s.Date == date && s.RuleId == rule && s.Metric == metric) is { } row)
            {
                row.Count += count;
                return;
            }

            row = new StatsDaily { AccountId = account.Id, UserId = account.UserId, Date = date, RuleId = rule, Metric = metric, Count = count };
            rows.Add(row);
            db.StatsDaily.Add(row);
        }

        for (var back = 0; back <= 29; back++)
        {
            var date = today.AddDays(-back);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var share = back == 0 ? 2 : 1;
            var counts = new Dictionary<Guid, int>
            {
                [vip] = (weekend ? random.Next(0, 2) : random.Next(2, 7)) / share,
                [invoices] = random.Next(0, 4) / share,
                [shops] = random.Next(0, weekend ? 6 : 3) / share,
                [newsletters] = random.Next(weekend ? 9 : 6, weekend ? 18 : 14) / share,
                [autoReplies] = (weekend ? 0 : random.Next(0, 3)) / share,
            };
            var matched = counts.Values.Sum();
            Add(date, Guid.Empty, StatsMetrics.Processed, matched + (random.Next(weekend ? 6 : 18, weekend ? 14 : 35) / share));
            Add(date, Guid.Empty, StatsMetrics.Matched, matched);
            foreach (var (rule, count) in counts)
            {
                Add(date, rule, StatsMetrics.Matched, count);
            }

            Add(date, vip, "Flag", counts[vip]);
            Add(date, vip, "AddKeyword", counts[vip]);
            Add(date, invoices, "AddKeyword", counts[invoices]);
            Add(date, invoices, "MoveTo", counts[invoices]);
            Add(date, shops, "MoveTo", counts[shops]);
            Add(date, newsletters, "MarkRead", counts[newsletters]);
            Add(date, newsletters, "MoveTo", counts[newsletters]);
            Add(date, autoReplies, "MarkRead", counts[autoReplies]);
            Add(date, autoReplies, "MoveTo", counts[autoReplies]);
        }

        await db.SaveChangesAsync(Ct);
    }

    private static int _messageId;

    private static MessageSnapshot Message(string fromAddress, string fromName, string subject, DateTimeOffset date, string to = Email,
        bool newsletter = false, bool seen = false, string? attachment = null, bool autoReply = false) => new()
    {
        MessageId = $"demo{Interlocked.Increment(ref _messageId)}@mailnix.example",
        From = new EmailAddress(fromAddress, fromName),
        To = [new EmailAddress(to, "Alex Morgan")],
        Subject = subject,
        Date = date,
        ReceivedAt = date,
        Size = attachment is null ? 18_000 : 240_000,
        IsSeen = seen,
        Headers =
        [
            .. newsletter ? new KeyValuePair<string, string>[] { new("List-Unsubscribe", $"<mailto:unsubscribe@{fromAddress.Split('@')[1]}>"), new("Precedence", "bulk") } : [],
            .. autoReply ? new KeyValuePair<string, string>[] { new("Auto-Submitted", "auto-replied") } : [],
        ],
        Attachments = attachment is null ? [] : [new AttachmentInfo(attachment, "application/pdf", 182_000)],
    };

    /// <summary>Older mail in Archive: the rule editor's preview runs on it.</summary>
    private static IEnumerable<MessageSnapshot> ArchiveMail(DateTimeOffset now) =>
    [
        Message("newsletter@devweekly.example", "Dev Weekly", "Dev Weekly #212: The state of WebAssembly", now.AddDays(-2).AddHours(-3), newsletter: true, seen: true),
        Message("priya@northwind.example", "Priya Shah", "Contract draft for the Q1 rollout", now.AddDays(-2).AddHours(-6), seen: true),
        Message("deals@gearshop.example", "GearShop", "Autumn sale: 30 % off all backpacks", now.AddDays(-3), newsletter: true),
        Message("news@notely.example", "Notely", "What's new in Notely: offline mode is here", now.AddDays(-4).AddHours(-2), newsletter: true),
        Message("sam.lee@example.org", "Sam Lee", "Photos from the weekend", now.AddDays(-4).AddHours(-7), seen: true),
        Message("updates@northwind.example", "Northwind Updates", "Northwind partner newsletter – October", now.AddDays(-5), newsletter: true, seen: true),
        Message("billing@cloudhost.example", "Cloudhost Billing", "Your invoice for September", now.AddDays(-6), attachment: "invoice-2026-09.pdf", seen: true),
        Message("hello@travelly.example", "Travelly", "5 city breaks for the autumn holidays", now.AddDays(-7), newsletter: true),
        Message("jordan@contoso.example", "Jordan Blake", "Re: Workshop slides", now.AddDays(-8), seen: true),
        Message("digest@forum.example", "Forum Digest", "Your weekly digest: 18 new replies", now.AddDays(-9), newsletter: true, seen: true),
        Message("team@meetup.example", "Meetup", "New event: Rust User Group, Nov 12", now.AddDays(-10), newsletter: true),
        Message("mia@example.net", "Mia Fischer", "Dinner on Friday?", now.AddDays(-11), seen: true),
    ];

    /// <summary>The inbox before the first "Run now".</summary>
    private static IEnumerable<MessageSnapshot> InboxMail(DateTimeOffset now) =>
    [
        Message("billing@cloudhost.example", "Cloudhost Billing", "Your invoice for October", now.AddHours(-20), attachment: "invoice-2026-10.pdf"),
        Message("newsletter@devweekly.example", "Dev Weekly", "Dev Weekly #213: Faster builds with less config", now.AddHours(-18), newsletter: true),
        Message("orders@gearshop.example", "GearShop", "Your order #48213 has shipped", now.AddHours(-16), to: "alex+gearshop@example.com"),
        Message("priya@northwind.example", "Priya Shah", "Q4 planning – agenda for Monday", now.AddHours(-12)),
        Message("receipts@payflow.example", "Payflow", "Receipt for your payment to Cloudhost", now.AddHours(-9), attachment: "receipt-7731.pdf"),
        Message("orders@bookbarn.example", "BookBarn", "Your books are on their way", now.AddHours(-7), to: "alex+books@example.com"),
        Message("sam.lee@example.org", "Sam Lee", "Lunch on Thursday?", now.AddHours(-5)),
        Message("news@notely.example", "Notely", "Tips: 7 shortcuts you might not know", now.AddHours(-3), newsletter: true),
    ];

    /// <summary>Arrives while mailnix watches the inbox.</summary>
    private static IEnumerable<MessageSnapshot> NewMail() =>
    [
        Message("jamie@partner.example", "Jamie Ortiz", "Out of office: back on Monday", DateTimeOffset.UtcNow, autoReply: true),
        Message("deals@gearshop.example", "GearShop", "Last chance: the autumn sale ends tonight", DateTimeOffset.UtcNow, newsletter: true),
        Message("legal@fabrikam.example", "Fabrikam Legal", "Signed NDA attached", DateTimeOffset.UtcNow, attachment: "nda-signed.pdf"),
    ];

    /// <summary>The rule editor's preview reads the same fake mailbox.</summary>
    private sealed class Reader(FakeMailServer mail) : IMessageReader
    {
        public Task<FolderSample> ReadLatestAsync(ImapConnectionSettings settings, string folder, int limit, FetchRequirements requirements, CancellationToken ct)
        {
            if (!mail.HasFolder(folder))
            {
                throw new Mail.FolderNotFoundException(folder);
            }

            var messages = mail.Messages(folder);
            return Task.FromResult(new FolderSample(folder, messages.Count, [.. messages.OrderByDescending(m => m.Uid).Take(limit)]));
        }

        public Task<IReadOnlySet<string>> ReadMessageIdsAsync(ImapConnectionSettings settings, string folder, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    /// <summary>The folder list of the fake server, as the account wizard and the folder pickers see it.</summary>
    private sealed class Probe(FakeMailServer mail) : IMailboxProbe
    {
        private static readonly Dictionary<string, SpecialFolderKind> Special = new()
        {
            ["INBOX"] = SpecialFolderKind.Inbox,
            ["Sent"] = SpecialFolderKind.Sent,
            ["Drafts"] = SpecialFolderKind.Drafts,
            ["Trash"] = SpecialFolderKind.Trash,
            ["Junk"] = SpecialFolderKind.Junk,
            ["Archive"] = SpecialFolderKind.Archive,
        };

        public Task<MailboxProbeResult> ProbeAsync(ImapConnectionSettings settings, bool includeCounts, CancellationToken cancellationToken)
        {
            var folders = mail.FolderPaths().Select(path =>
            {
                var slash = path.LastIndexOf('/');
                var messages = mail.Messages(path);
                return new MailFolderInfo(
                    path,
                    slash < 0 ? path : path[(slash + 1)..],
                    slash < 0 ? null : path[..slash],
                    CanSelect: true,
                    Special.TryGetValue(path, out var kind) ? kind : null,
                    includeCounts ? messages.Count : null,
                    includeCounts ? messages.Count(m => !m.IsSeen) : null);
            }).ToList();
            var capabilities = new MailboxCapabilities(true, true, false, false, true, true, false, false, ["IMAP4rev1", "IDLE", "MOVE", "UIDPLUS", "SPECIAL-USE"]);
            return Task.FromResult(new MailboxProbeResult(capabilities, '/', folders, new SpecialFolderMap("Sent", "Drafts", "Trash", "Junk", "Archive")));
        }
    }
}
