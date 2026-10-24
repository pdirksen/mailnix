using System.Text;
using MimeKit;
using MimeKit.Text;

namespace Mailnix.Core.Messages;

public static class MessageSnapshotFactory
{
    /// <summary>Builds a complete snapshot (headers, attachments and body included) from a parsed message.</summary>
    public static MessageSnapshot FromMimeMessage(
        MimeMessage message,
        uint uid = 0,
        string folder = "INBOX",
        DateTimeOffset? receivedAt = null,
        bool isSeen = false,
        bool isFlagged = false,
        IReadOnlyList<string>? keywords = null)
    {
        return new MessageSnapshot
        {
            Uid = uid,
            Folder = folder,
            MessageId = message.MessageId,
            From = Addresses(message.From) is [var first, ..] ? first : null,
            To = Addresses(message.To),
            Cc = Addresses(message.Cc),
            ReplyTo = Addresses(message.ReplyTo),
            Subject = message.Subject,
            Date = message.Date == DateTimeOffset.MinValue ? null : message.Date,
            ReceivedAt = receivedAt,
            Size = MeasureSize(message),
            IsSeen = isSeen,
            IsFlagged = isFlagged,
            Keywords = keywords ?? [],
            Headers = [.. message.Headers.Select(h => new KeyValuePair<string, string>(h.Field, h.Value))],
            Attachments = [.. message.Attachments.OfType<MimePart>().Select(ToAttachment)],
            BodyText = message.TextBody ?? HtmlToText(message.HtmlBody),
        };
    }

    public static IReadOnlyList<EmailAddress> Addresses(InternetAddressList list) =>
        [.. list.Mailboxes.Where(m => !string.IsNullOrWhiteSpace(m.Address)).Select(m => new EmailAddress(m.Address, m.Name))];

    private static AttachmentInfo ToAttachment(MimePart part)
    {
        long size = 0;
        if (part.Content?.Stream is { CanSeek: true } stream)
        {
            size = stream.Length;
        }

        return new AttachmentInfo(part.FileName, part.ContentType.MimeType.ToLowerInvariant(), size);
    }

    /// <summary>Visible text of an HTML body: text nodes outside script/style, line breaks at block elements.</summary>
    internal static string? HtmlToText(string? html)
    {
        if (html is null)
        {
            return null;
        }

        var text = new StringBuilder();
        var hidden = 0;
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken { Id: HtmlTagId.Script or HtmlTagId.Style } tag when !tag.IsEmptyElement:
                    hidden += tag.IsEndTag ? -1 : 1;
                    hidden = Math.Max(hidden, 0);
                    break;
                case HtmlTagToken { Id: HtmlTagId.Br or HtmlTagId.P or HtmlTagId.Div or HtmlTagId.TR or HtmlTagId.LI or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 }:
                    text.Append('\n');
                    break;
                case HtmlDataToken data when hidden == 0:
                    text.Append(data.Data); // the tokenizer already decodes character references
                    break;
            }
        }

        return text.ToString();
    }

    private static long MeasureSize(MimeMessage message)
    {
        using var counter = new MimeKit.IO.MeasuringStream();
        message.WriteTo(counter);
        return counter.Length;
    }
}
