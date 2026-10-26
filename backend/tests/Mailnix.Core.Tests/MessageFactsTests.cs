using Mailnix.Core.Messages;
using static Mailnix.Core.Tests.TestData;

namespace Mailnix.Core.Tests;

public sealed class MessageFactsTests
{
    [Fact]
    public void Newsletter_is_detected_with_its_list_id()
    {
        var facts = Facts("newsletter.eml");

        facts.IsNewsletter.ShouldBeTrue();
        facts.ListId.ShouldBe("news.shop.example");
        facts.IsAutoReply.ShouldBeFalse();
        facts.IsReply.ShouldBeFalse();
    }

    [Fact]
    public void Snapshot_decodes_subject_and_turns_html_into_visible_text()
    {
        var message = Fixture("newsletter.eml");

        message.Subject.ShouldBe("Unsere Herbst-Angebote 🍂");
        message.From!.Domain.ShouldBe("mail.shop.example");
        message.BodyText.ShouldNotBeNull();
        message.BodyText.ShouldContain("Herbst & Winter");
        message.BodyText.ShouldNotContain("track()");
        message.BodyText.ShouldNotContain("color: red");
    }

    [Theory]
    [InlineData("google-invite.eml", "REQUEST")]
    [InlineData("outlook-invite.eml", "REQUEST")]
    [InlineData("outlook-reply.eml", "REPLY")]
    public void Calendar_messages_are_detected_wherever_the_calendar_part_is(string fixture, string method)
    {
        var facts = Facts(fixture);

        facts.Message.CalendarMethod.ShouldBe(method);
        facts.IsCalendarMessage.ShouldBeTrue();
    }

    [Fact]
    public void Outlook_invitations_have_no_calendar_attachment()
    {
        // The reason attachment conditions miss them: the calendar part is an alternative body, not a file.
        Fixture("outlook-invite.eml").Attachments.ShouldBeEmpty();
        Fixture("google-invite.eml").Attachments!.Single().FileName.ShouldBe("invite.ics");
    }

    [Fact]
    public void Exchange_content_class_marks_calendar_messages_without_a_calendar_part()
    {
        // Older Outlook wraps the invitation in winmail.dat (TNEF), which hides the calendar part.
        var tnef = Simple() with
        {
            Headers = [new("Content-Class", "urn:content-classes:calendarmessage")],
            Attachments = [new AttachmentInfo("winmail.dat", "application/ms-tnef", 4000)],
        };

        new MessageFacts(tnef, Context()).IsCalendarMessage.ShouldBeTrue();
        new MessageFacts(Simple() with { CalendarMethod = "CANCEL", Headers = [] }, Context()).IsCalendarMessage.ShouldBeTrue();
        Fixture("cc-only.eml").CalendarMethod.ShouldBeNull();
        Facts("cc-only.eml").IsCalendarMessage.ShouldBeFalse();
    }

    [Theory]
    [InlineData("PUBLISH")]
    [InlineData("")]
    public void Appointment_files_on_booking_confirmations_are_not_calendar_messages(string method)
    {
        // E.g. a train ticket with an .ics to add the journey to the calendar: it belongs to travel, not to invitations.
        var booking = Simple() with { CalendarMethod = method, Headers = [], Attachments = [new AttachmentInfo("ticket.ics", "text/calendar", 900)] };

        new MessageFacts(booking, Context()).IsCalendarMessage.ShouldBeFalse();
    }

    [Fact]
    public void Calendar_parts_without_a_method_count_with_an_empty_method()
    {
        MessageSnapshotFactory.CalendarMethod([(new MimeKit.ContentType("application", "octet-stream"), "meeting.ICS")]).ShouldBe(string.Empty);
        MessageSnapshotFactory.CalendarMethod([(new MimeKit.ContentType("text", "plain"), null)]).ShouldBeNull();
    }

    [Fact]
    public void German_out_of_office_reply_is_an_auto_reply()
    {
        var facts = Facts("out-of-office.eml");

        facts.IsAutoReply.ShouldBeTrue();
        facts.IsReply.ShouldBeTrue();
        facts.IsNewsletter.ShouldBeFalse();
    }

    [Fact]
    public void Auto_reply_is_recognized_by_subject_alone()
    {
        // The subject is part of the envelope, so this works even without fetched headers.
        new MessageFacts(Simple(subject: "Automatische Antwort: Rechnung"), Context()).IsAutoReply.ShouldBeTrue();
        new MessageFacts(Simple(subject: "Out of Office: back Monday"), Context()).IsAutoReply.ShouldBeTrue();
        new MessageFacts(Simple(subject: "Re: Out of office plans"), Context()).IsAutoReply.ShouldBeFalse();
    }

    [Fact]
    public void Authentication_results_come_from_the_topmost_header_only()
    {
        // phishing.eml has a forged second header claiming everything passed.
        var auth = Facts("phishing.eml").Authentication;

        auth.Spf.ShouldBe("fail");
        auth.Dkim.ShouldBe("none");
        auth.Dmarc.ShouldBe("fail");
    }

    [Fact]
    public void Authentication_results_can_be_limited_to_trusted_servers()
    {
        var context = Context(c => c with { TrustedAuthServIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.example" } });

        Facts("newsletter.eml", context).Authentication.ShouldBe(new AuthResults("none", "none", "none"));
        Facts("newsletter.eml").Authentication.ShouldBe(new AuthResults("pass", "pass", "pass"));
    }

    [Fact]
    public void Display_name_with_a_foreign_address_is_a_mismatch()
    {
        Facts("phishing.eml").DisplayNameMismatch.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Amazon.de <shipment@amazon.de>")]
    [InlineData("amazon.de <bounce@marketplace.amazon.de>")]
    [InlineData("Dr. Erika Mustermann <erika@example.org>")]
    [InlineData("support@example.org <noreply@example.org>")]
    public void Matching_or_plain_display_names_are_fine(string from)
    {
        new MessageFacts(Simple(from: from), Context()).DisplayNameMismatch.ShouldBeFalse();
    }

    [Theory]
    [InlineData("phishing.eml", 8.4)]
    [InlineData("newsletter.eml", 1.2)]
    [InlineData("reply.eml", -2.1)]
    [InlineData("cc-only.eml", 0.4)]
    public void Spam_score_is_read_from_common_headers(string fixture, double expected)
    {
        Facts(fixture).SpamScore.ShouldNotBeNull().ShouldBe(expected, tolerance: 0.001);
    }

    [Fact]
    public void Missing_spam_score_is_null()
    {
        Facts("out-of-office.eml").SpamScore.ShouldBeNull();
    }

    [Fact]
    public void Reply_to_my_mail_needs_a_known_sent_message_id()
    {
        Facts("reply.eml").IsReplyToMyMail.ShouldBeFalse();

        var context = Context(c => c with { SentMessageIds = new HashSet<string> { "sent-42@example.com" } });
        Facts("reply.eml", context).IsReplyToMyMail.ShouldBeTrue();
        Facts("out-of-office.eml", context).IsReplyToMyMail.ShouldBeFalse();
    }

    [Fact]
    public void Plus_tags_and_delivered_to_are_extracted()
    {
        var facts = Facts("plus-addressed.eml");

        facts.PlusTags.ShouldBe(["shop"]);
        facts.DeliveredTo.Select(a => a.Address).ShouldBe(["me+shop@example.com"]);
        facts.IAmOnlyInCcOrBcc.ShouldBeFalse(); // me+shop@ counts as my address
        facts.RecipientCount.ShouldBe(2);
    }

    [Fact]
    public void Mail_where_I_am_only_in_cc_is_recognized_case_insensitively()
    {
        Facts("cc-only.eml").IAmOnlyInCcOrBcc.ShouldBeTrue();
        Facts("newsletter.eml").IAmOnlyInCcOrBcc.ShouldBeFalse();
    }

    [Fact]
    public void Attachments_are_listed_with_normalized_extensions()
    {
        var attachments = Fixture("plus-addressed.eml").Attachments!;

        attachments.Select(a => (a.FileName, a.Extension, a.MimeType)).ShouldBe(
        [
            ("Invoice-2026-1042.pdf", "pdf", "application/pdf"),
            ("Photo.JPG", "jpg", "image/jpeg"),
        ]);
    }

    [Fact]
    public void Addresses_are_normalized_including_idn_domains()
    {
        var address = new EmailAddress("Info@Müller-Shop.DE", "Müller");

        address.Address.ShouldBe("info@xn--mller-shop-9db.de");
        address.Domain.ShouldBe("xn--mller-shop-9db.de");
        EmailAddress.IsDomainOrSubdomain("mail.example.com", "example.com").ShouldBeTrue();
        EmailAddress.IsDomainOrSubdomain("evilexample.com", "example.com").ShouldBeFalse();
    }

    [Theory]
    [InlineData(Mailnix.Core.Rules.NamedListKind.Domain, "müller.de")]
    [InlineData(Mailnix.Core.Rules.NamedListKind.Address, "Info@Müller.de")]
    [InlineData(Mailnix.Core.Rules.NamedListKind.Pattern, "*@*.müller.de")]
    public void List_entries_display_in_unicode_and_normalize_back(Mailnix.Core.Rules.NamedListKind kind, string entry)
    {
        var stored = Mailnix.Core.Rules.NamedList.Normalize(kind, entry);
        var display = Mailnix.Core.Rules.NamedList.ToDisplay(kind, stored);

        Mailnix.Core.Rules.NamedList.Normalize(kind, display).ShouldBe(stored);
        if (kind != Mailnix.Core.Rules.NamedListKind.Pattern)
        {
            display.ShouldContain("müller.de");
        }
    }
}
