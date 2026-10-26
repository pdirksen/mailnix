using Mailnix.Core.Messages;
using Mailnix.Core.Rules;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class FolderTemplateTests
{
    private static string Resolve(string template, MessageSnapshot? message = null, char delimiter = '.') =>
        FolderTemplate.Resolve(template, new MessageFacts(message ?? Fixture("newsletter.eml"), Context()), Context(c => c with { FolderDelimiter = delimiter }));

    [Fact]
    public void Placeholders_are_filled_and_levels_use_the_server_delimiter()
    {
        Resolve("INBOX/Archive/{year}/{month}").ShouldBe("INBOX.Archive.2026.10");
        Resolve("INBOX/Archive/{year}/{month}", delimiter: '/').ShouldBe("INBOX/Archive/2026/10");
        Resolve("Lists/{listId}").ShouldBe("Lists.news_shop_example");
    }

    [Fact]
    public void The_static_prefix_ends_before_the_first_placeholder()
    {
        FolderTemplate.StaticPrefix("INBOX/Archive/{year}/{month}", '.').ShouldBe("INBOX.Archive");
        FolderTemplate.StaticPrefix("INBOX/ Newsletters ", '/').ShouldBe("INBOX/Newsletters");
        FolderTemplate.StaticPrefix("INBOX/News-{year}", '/').ShouldBe("INBOX");
        FolderTemplate.StaticPrefix("{fromDomain}/Mail", '/').ShouldBeNull();
    }

    [Fact]
    public void Placeholder_values_never_create_extra_levels()
    {
        // "mail.shop.example" must stay one folder on a server whose delimiter is ".".
        Resolve("Domains/{fromDomain}").ShouldBe("Domains.mail_shop_example");
        Resolve("Domains/{fromDomain}", delimiter: '/').ShouldBe("Domains/mail.shop.example");
        Resolve("People/{fromName}", Simple(from: "\"A/B * 50%\" <ab@example.org>"), '/').ShouldBe("People/A_B _ 50_");
    }

    [Fact]
    public void Dates_use_the_users_time_zone()
    {
        // 23:30 UTC on Dec 31 is already the new year in Berlin.
        var message = Simple(receivedAt: new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero));

        Resolve("{year}-{month}-{day}", message).ShouldBe("2027-01-01");
    }

    [Fact]
    public void Missing_values_become_an_underscore()
    {
        Resolve("Shops/{plusTag}", Simple()).ShouldBe("Shops._");
        Resolve("Shops/{plusTag}", Fixture("plus-addressed.eml")).ShouldBe("Shops.shop");
    }

    [Theory]
    [InlineData("INBOX/Archive/{year}")]
    [InlineData("Domains/{ fromDomain }")]
    [InlineData("INBOX.Shopping")]
    public void Valid_templates_have_no_errors(string template)
    {
        FolderTemplate.Validate(template).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("Archive/{year", "Unclosed")]
    [InlineData("Archive/year}", "Unexpected")]
    [InlineData("Archive/{colour}", "Unknown placeholder {colour}")]
    [InlineData("Archive//{year}", "must not be empty")]
    [InlineData("/Archive", "must not be empty")]
    public void Invalid_templates_are_explained(string template, string expected)
    {
        FolderTemplate.Validate(template).ShouldContain(e => e.Contains(expected, StringComparison.Ordinal));
    }
}
