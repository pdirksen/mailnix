using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mailnix.Core.Rules;
using MimeKit;
using MimeKit.Cryptography;

namespace Mailnix.Core.Messages;

/// <summary>Facts derived from a message's headers, computed on first use and cached for one evaluation.</summary>
public sealed partial class MessageFacts(MessageSnapshot message, EvaluationContext context)
{
    private static readonly string[] AutoReplySubjectPrefixes =
    [
        "Out of Office", "Out of the office", "Automatic reply", "Auto-Reply", "Autoreply", "Auto:",
        "Abwesenheitsnotiz", "Automatische Antwort", "Abwesend",
    ];

    private static readonly string[] DeliveredToHeaders = ["Delivered-To", "X-Original-To", "Envelope-To"];

    private bool? _isNewsletter;
    private bool? _isAutoReply;
    private bool? _isReplyToMyMail;
    private AuthResults? _auth;
    private bool? _displayNameMismatch;
    private (bool Parsed, double? Value) _spamScore;
    private IReadOnlyList<EmailAddress>? _deliveredTo;
    private IReadOnlyList<string>? _plusTags;

    public MessageSnapshot Message => message;

    /// <summary>Mailing lists and bulk mail: List-Unsubscribe/List-Id headers or Precedence bulk/list/junk.</summary>
    public bool IsNewsletter => _isNewsletter ??=
        message.FirstHeader("List-Unsubscribe") is not null
        || message.FirstHeader("List-Id") is not null
        || message.FirstHeader("Precedence")?.Trim().ToLowerInvariant() is "bulk" or "list" or "junk";

    /// <summary>The list identifier from List-Id, e.g. "news.example.com" for "Example News &lt;news.example.com&gt;".</summary>
    public string? ListId
    {
        get
        {
            var raw = message.FirstHeader("List-Id");
            if (raw is null)
            {
                return null;
            }

            var start = raw.LastIndexOf('<');
            var end = raw.LastIndexOf('>');
            return (start >= 0 && end > start ? raw[(start + 1)..end] : raw).Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Invitations, replies and cancellations from a calendar: a calendar part anywhere in the message, or the
    /// Content-Class Exchange sets on meeting messages (also when the invitation is wrapped in winmail.dat).
    /// </summary>
    public bool IsCalendarMessage =>
        message.CalendarMethod is not null
        || message.FirstHeader("Content-Class")?.Contains("calendarmessage", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Out-of-office replies and other automatic responses.</summary>
    public bool IsAutoReply => _isAutoReply ??= DetectAutoReply();

    /// <summary>A reply to some message (has In-Reply-To or References).</summary>
    public bool IsReply => message.FirstHeader("In-Reply-To") is not null || message.FirstHeader("References") is not null;

    /// <summary>A reply to a message the user sent (referenced Message-ID found in the Sent folder).</summary>
    public bool IsReplyToMyMail => _isReplyToMyMail ??=
        context.SentMessageIds.Count > 0
        && ReferencedMessageIds().Any(context.SentMessageIds.Contains);

    public AuthResults Authentication => _auth ??= ReadAuthenticationResults();

    /// <summary>The display name shows an address or domain unrelated to the actual sender, e.g. "service@paypal.com" &lt;x@example.ru&gt;.</summary>
    public bool DisplayNameMismatch => _displayNameMismatch ??= DetectDisplayNameMismatch();

    /// <summary>From X-Spam-Score, X-Spam-Status (score=…), X-Rspamd-Score or X-Spamd-Result; null if absent.</summary>
    public double? SpamScore
    {
        get
        {
            if (!_spamScore.Parsed)
            {
                _spamScore = (true, ReadSpamScore());
            }

            return _spamScore.Value;
        }
    }

    /// <summary>None of the user's own addresses is in To (they got it via Cc, Bcc or a list).</summary>
    public bool IAmOnlyInCcOrBcc =>
        context.MyAddresses.Count > 0
        && !message.To.Any(IsMine);

    /// <summary>Envelope recipients as recorded by the receiving server (Delivered-To, X-Original-To, Envelope-To).</summary>
    public IReadOnlyList<EmailAddress> DeliveredTo => _deliveredTo ??=
    [
        .. DeliveredToHeaders
            .SelectMany(message.HeaderValues)
            .Select(v => MailboxAddress.TryParse(v.Trim(), out var mailbox) ? mailbox : null)
            .OfType<MailboxAddress>()
            .Where(m => !string.IsNullOrWhiteSpace(m.Address))
            .Select(m => new EmailAddress(m.Address))
            .DistinctBy(a => a.Address),
    ];

    /// <summary>Plus tags of the recipient addresses (only the user's own addresses when they are known).</summary>
    public IReadOnlyList<string> PlusTags => _plusTags ??=
    [
        .. message.To.Concat(message.Cc).Concat(DeliveredTo)
            .Where(a => a.PlusTag is not null && (context.MyAddresses.Count == 0 || context.MyAddresses.Contains(a.BaseAddress)))
            .Select(a => a.PlusTag!)
            .Distinct(StringComparer.Ordinal),
    ];

    public int RecipientCount => message.To.Count + message.Cc.Count;

    private bool IsMine(EmailAddress address) =>
        context.MyAddresses.Contains(address.Address) || context.MyAddresses.Contains(address.BaseAddress);

    private bool DetectAutoReply()
    {
        var autoSubmitted = message.FirstHeader("Auto-Submitted")?.Trim();
        if (autoSubmitted is not null && !autoSubmitted.StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.FirstHeader("X-Autoreply") is not null
            || message.FirstHeader("X-Autorespond") is not null
            || message.FirstHeader("Precedence")?.Trim().Equals("auto_reply", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var subject = message.Subject?.TrimStart() ?? string.Empty;
        return AutoReplySubjectPrefixes.Any(p => subject.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> ReferencedMessageIds() =>
        message.HeaderValues("In-Reply-To").Concat(message.HeaderValues("References"))
            .SelectMany(v => MessageIdToken().Matches(v).Select(m => m.Groups[1].Value));

    private AuthResults ReadAuthenticationResults()
    {
        var parsed = message.HeaderValues("Authentication-Results")
            .Select(v => AuthenticationResults.TryParse(Encoding.UTF8.GetBytes(v), out var r) ? r : null)
            .OfType<AuthenticationResults>()
            .ToList();

        // Without configured servers, only the topmost header is trustworthy: the receiving server adds it,
        // anything below may have been written by the sender.
        var trusted = context.TrustedAuthServIds.Count == 0
            ? parsed.Take(1)
            : parsed.Where(r => r.AuthenticationServiceIdentifier is { } id && context.TrustedAuthServIds.Contains(id));
        var results = trusted.SelectMany(r => r.Results).ToList();

        string Result(string method, bool anyPassWins = false)
        {
            var values = results
                .Where(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Result.ToLowerInvariant())
                .ToList();
            if (values.Count == 0)
            {
                return "none";
            }

            return anyPassWins && values.Contains("pass") ? "pass" : values[0];
        }

        // A message can carry several DKIM signatures; one valid one is enough.
        return new AuthResults(Result("spf"), Result("dkim", anyPassWins: true), Result("dmarc"));
    }

    private bool DetectDisplayNameMismatch()
    {
        if (message.From is not { Name: { } name, Domain: { Length: > 0 } senderDomain })
        {
            return false;
        }

        foreach (Match match in DomainInName().Matches(name))
        {
            var shown = EmailAddress.NormalizeDomain(match.Groups["domain"].Value);
            if (!EmailAddress.IsDomainOrSubdomain(senderDomain, shown) && !EmailAddress.IsDomainOrSubdomain(shown, senderDomain))
            {
                return true;
            }
        }

        return false;
    }

    private double? ReadSpamScore()
    {
        if (ParseNumber(message.FirstHeader("X-Spam-Score")) is { } score)
        {
            return score;
        }

        if (message.FirstHeader("X-Spam-Status") is { } status && SpamStatusScore().Match(status) is { Success: true } statusMatch)
        {
            return ParseNumber(statusMatch.Groups[1].Value);
        }

        if (ParseNumber(message.FirstHeader("X-Rspamd-Score")) is { } rspamd)
        {
            return rspamd;
        }

        return message.FirstHeader("X-Spamd-Result") is { } spamd && SpamdResultScore().Match(spamd) is { Success: true } spamdMatch
            ? ParseNumber(spamdMatch.Groups[1].Value)
            : null;
    }

    private static double? ParseNumber(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    [GeneratedRegex(@"<([^<>\s]+)>")]
    private static partial Regex MessageIdToken();

    // An address ("x@paypal.com") or a bare domain ("paypal.com") inside a display name.
    [GeneratedRegex(@"(?:[\w.+-]+@)?(?<domain>(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex DomainInName();

    [GeneratedRegex(@"score=(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase, 100)]
    private static partial Regex SpamStatusScore();

    [GeneratedRegex(@"\[\s*(-?\d+(?:\.\d+)?)\s*/", RegexOptions.None, 100)]
    private static partial Regex SpamdResultScore();
}

/// <summary>SPF, DKIM and DMARC outcome, lowercase RFC 8601 result values ("none" when absent).</summary>
public sealed record AuthResults(string Spf, string Dkim, string Dmarc);
