using Mailnix.Mail.Email;

namespace Mailnix.Integration.Tests.Fakes;

/// <summary>Collects the emails mailnix would send; can pretend the SMTP server is missing or failing.</summary>
internal sealed class FakeEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public bool IsConfigured { get; set; } = true;

    /// <summary>When set, sending fails with this error.</summary>
    public EmailException? Failure { get; set; }

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        lock (_sent)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Waits for the background dispatcher to deliver an email matching <paramref name="match"/>.</summary>
    public async Task<EmailMessage> WaitForAsync(Func<EmailMessage, bool> match, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (Sent.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(25, ct);
        }

        throw new TimeoutException($"No matching email; sent so far: {string.Join(", ", Sent.Select(m => $"{m.To}: {m.Subject}"))}");
    }
}
