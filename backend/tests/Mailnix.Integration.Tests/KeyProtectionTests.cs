using Mailnix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

/// <summary>Encryption keys are stored encrypted on Windows, and existing unprotected keys are upgraded.</summary>
public sealed class KeyProtectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> KeyXmlAsync(MailnixAppFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().DataProtectionKeys.Select(k => k.Xml!).ToListAsync(Ct);
    }

    [Fact]
    public async Task New_keys_are_protected_with_dpapi_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is only available on Windows.");
        await using var app = new MailnixAppFactory();
        (await app.CreateApiClient().RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();

        var keys = await KeyXmlAsync(app);
        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(xml => xml.Contains("<encryptedSecret") && xml.Contains("DpapiXmlDecryptor"));
    }

    [Fact]
    public async Task Existing_unprotected_keys_are_replaced_and_mailbox_passwords_stay_readable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is only available on Windows.");
        var database = Path.Combine(Path.GetTempPath(), $"mailnix-keys-{Guid.NewGuid():N}.db");
        try
        {
            Guid accountId;
            string storedBefore;
            await using (var before = new MailnixAppFactory()
                .WithSetting("Mailnix:Database:ConnectionString", $"Data Source={database};Pooling=False")
                .WithSetting("Mailnix:DataProtection:KeyProtection", "None"))
            {
                (await before.CreateApiClient().RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
                accountId = await Seed.AccountAsync(before, "owner@example.com");
                (await KeyXmlAsync(before)).ShouldAllBe(xml => !xml.Contains("<encryptedSecret"));
                await using var scope = before.Services.CreateAsyncScope();
                storedBefore = (await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(a => a.Id == accountId, Ct)).EncryptedSecret;
            }

            await using var after = new MailnixAppFactory()
                .WithSetting("Mailnix:Database:ConnectionString", $"Data Source={database};Pooling=False")
                .WithSetting("Mailnix:DataProtection:KeyProtection", "WindowsUser");
            after.CreateApiClient(); // starts the app, which runs the upgrade

            var keys = await KeyXmlAsync(after);
            keys.ShouldNotBeEmpty();
            keys.ShouldAllBe(xml => xml.Contains("<encryptedSecret"));

            await using var check = after.Services.CreateAsyncScope();
            var account = await check.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(a => a.Id == accountId, Ct);
            account.EncryptedSecret.ShouldNotBe(storedBefore);
            check.ServiceProvider.GetRequiredService<MailSecretProtector>().Unprotect(account.EncryptedSecret).ShouldBe("secret");
        }
        finally
        {
            File.Delete(database);
        }
    }
}
