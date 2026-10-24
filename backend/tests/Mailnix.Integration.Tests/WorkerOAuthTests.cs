using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Mailnix.Integration.Tests;

/// <summary>The mailbox watcher signs in to OAuth accounts with a current access token.</summary>
public sealed class WorkerOAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(MailnixAppFactory App, Guid AccountId)> StartAsync(FakeMailServer mail, FakeOAuthProvider provider)
    {
        var app = new MailnixAppFactory()
            .WithSetting("Mailnix:Worker:Enabled", "true")
            .WithSetting("Mailnix:Worker:ReconcileInterval", "00:00:00.200")
            .WithSetting("Mailnix:Worker:IdleTimeout", "00:00:01")
            .WithSetting("Mailnix:OAuth:Google:ClientId", "test-client")
            .WithSetting("Mailnix:OAuth:Google:ClientSecret", "test-secret")
            .WithServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IMailboxSessionFactory>(mail));
                s.Replace(ServiceDescriptor.Singleton(sp => new OAuthClient(new HttpClient(provider), sp.GetRequiredService<IOptions<OAuthOptions>>(), sp.GetRequiredService<TimeProvider>())));
            });
        (await app.CreateApiClient().RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        // Created as a Google account in one step, so the watcher never sees anything else.
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailnixDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == "owner@example.com", Ct);
        var accountId = Guid.CreateVersion7();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            TenantId = user.TenantId,
            UserId = user.Id,
            Name = "Gmail",
            Provider = MailProvider.Google,
            Host = "imap.gmail.com",
            Port = 993,
            Security = ImapSecurity.SslOnConnect,
            Username = "user@gmail.com",
            EncryptedSecret = scope.ServiceProvider.GetRequiredService<MailSecretProtector>().Protect(provider.RefreshToken),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        return (app, accountId);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task The_watcher_connects_with_an_access_token()
    {
        var mail = new FakeMailServer();
        var (app, _) = await StartAsync(mail, new FakeOAuthProvider { AccessToken = "watcher-token" });
        await using (app)
        {
            await EventuallyAsync(() => Task.FromResult(mail.LastSettings is not null));

            mail.LastSettings!.Authentication.ShouldBe(ImapAuthentication.OAuth2);
            mail.LastSettings.Password.ShouldBe("watcher-token");
            mail.LastSettings.Username.ShouldBe("user@gmail.com");
        }
    }

    [Fact]
    public async Task A_revoked_grant_asks_for_reconnecting()
    {
        var mail = new FakeMailServer();
        var (app, accountId) = await StartAsync(mail, new FakeOAuthProvider { Revoked = true });
        await using (app)
        {
            await EventuallyAsync(async () =>
            {
                await using var scope = app.Services.CreateAsyncScope();
                var account = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(a => a.Id == accountId);
                return account.LastErrorCode == ConnectionErrorCode.ReconnectRequired;
            });
            mail.Connections.ShouldBe(0);
        }
    }
}
