using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

internal static class Seed
{
    /// <summary>Adds a mail account for an existing user directly in the database (no IMAP involved).</summary>
    public static async Task<Guid> AccountAsync(MailnixAppFactory app, string userEmail, string name = "Work", bool enabled = true)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == userEmail);
        var account = new MailAccount
        {
            Id = Guid.CreateVersion7(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Name = name,
            Host = "imap.example.com",
            Port = 993,
            Security = ImapSecurity.SslOnConnect,
            Username = userEmail,
            EncryptedSecret = scope.ServiceProvider.GetRequiredService<MailSecretProtector>().Protect("secret"),
            Status = MailAccountStatus.Ok,
            Enabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }
}
