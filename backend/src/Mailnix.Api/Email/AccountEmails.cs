using System.Globalization;
using System.Threading.Channels;
using Mailnix.Infrastructure.Identity;
using Mailnix.Mail.Email;

namespace Mailnix.Api.Email;

/// <summary>
/// Emails about accounts (password reset, invitations, security notices). They are queued and sent in the
/// background: requests return at once, and "forgot password" answers in the same time whether or not the address
/// has an account.
/// </summary>
public sealed class AccountEmails(IEmailSender sender, PublicLinks links, EmailOutbox outbox, TimeProvider time)
{
    /// <summary>How long a password-reset link works (the lifetime of Identity's data protection tokens).</summary>
    public static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(2);

    /// <summary>Emails can be sent at all.</summary>
    public bool CanSend => sender.IsConfigured;

    /// <summary>Emails with links can be sent (they need the public address).</summary>
    public bool CanSendLinks => sender.IsConfigured && links.IsConfigured;

    public void PasswordReset(AppUser user, string? fallbackLanguage, string token)
    {
        if (!CanSendLinks)
        {
            return;
        }

        var link = links.For("/reset-password", new Dictionary<string, string> { ["email"] = user.Email!, ["token"] = token });
        outbox.Enqueue(EmailTemplates.PasswordReset(user.Email!, Language(user, fallbackLanguage), link, ResetLinkLifetime));
    }

    public void PasswordChanged(AppUser user)
    {
        if (CanSend)
        {
            outbox.Enqueue(EmailTemplates.PasswordChanged(user.Email!, Language(user), Now(user), ForgotLink()));
        }
    }

    public void TwoFactorChanged(AppUser user, bool enabled)
    {
        if (CanSend)
        {
            outbox.Enqueue(EmailTemplates.TwoFactorChanged(user.Email!, Language(user), enabled, Now(user), ForgotLink()));
        }
    }

    /// <returns>Whether the invitation was queued.</returns>
    public bool Invitation(string to, AppUser inviter, string organization, string link, DateTimeOffset expiresAt)
    {
        if (!CanSendLinks)
        {
            return false;
        }

        var language = Language(inviter);
        outbox.Enqueue(EmailTemplates.Invitation(to, language, inviter.DisplayName ?? inviter.Email!, organization, link, Format(expiresAt, inviter)));
        return true;
    }

    /// <summary>Sends right away, so the System page can report what went wrong.</summary>
    public Task SendTestAsync(AppUser to, CancellationToken ct) => sender.SendAsync(EmailTemplates.Test(to.Email!, Language(to)), ct);

    private string? ForgotLink() => links.IsConfigured ? links.For("/forgot-password") : null;

    private static string Language(AppUser user, string? fallback = null) => user.Language ?? (fallback == "de" ? "de" : "en");

    private string Now(AppUser user) => Format(time.GetUtcNow(), user);

    /// <summary>"2026-10-28 14:05 (Europe/Berlin)" in the user's time zone.</summary>
    private static string Format(DateTimeOffset moment, AppUser user)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(user.TimeZone, out var found) ? found : TimeZoneInfo.Utc;
        return $"{TimeZoneInfo.ConvertTime(moment, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({user.TimeZone})";
    }
}

/// <summary>Queue of emails, sent one by one by <see cref="EmailDispatcher"/>.</summary>
public sealed class EmailOutbox
{
    private readonly Channel<EmailMessage> _queue = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(EmailMessage message) => _queue.Writer.TryWrite(message);

    public IAsyncEnumerable<EmailMessage> ReadAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);
}

internal sealed partial class EmailDispatcher(EmailOutbox outbox, IEmailSender sender, ILogger<EmailDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in outbox.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await sender.SendAsync(message, stoppingToken);
                    LogSent(message.Subject);
                }
                catch (EmailException ex)
                {
                    LogFailed(ex, message.Subject);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    // The recipient is left out of the logs on purpose.
    [LoggerMessage(Level = LogLevel.Information, Message = "Sent email \"{Subject}\"")]
    private partial void LogSent(string subject);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send email \"{Subject}\"")]
    private partial void LogFailed(Exception exception, string subject);
}
