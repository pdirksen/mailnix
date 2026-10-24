using System.Security.Cryptography;
using System.Xml.Linq;
using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

/// <summary>Encryption keys are stored encrypted (master key, or DPAPI on Windows), and existing unprotected keys are upgraded.</summary>
public sealed class KeyProtectionTests
{
    private static readonly string Key = MasterKey.Generate();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two app instances in a row on the same database, like a restart with changed settings.</summary>
    private static async Task WithDatabaseAsync(Func<Func<string, string?, MailnixAppFactory>, Task> test)
    {
        var database = Path.Combine(Path.GetTempPath(), $"mailnix-keys-{Guid.NewGuid():N}.db");
        try
        {
            await test((protection, masterKey) => new MailnixAppFactory()
                .WithSetting("Mailnix:Database:ConnectionString", $"Data Source={database};Pooling=False")
                .WithSetting("Mailnix:DataProtection:KeyProtection", protection)
                .WithSetting("Mailnix:DataProtection:MasterKey", masterKey));
        }
        finally
        {
            File.Delete(database);
        }
    }

    private static async Task<(Guid AccountId, string Stored)> SeedAccountAsync(MailnixAppFactory app)
    {
        (await app.CreateApiClient().RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        var accountId = await Seed.AccountAsync(app, "owner@example.com");
        await using var scope = app.Services.CreateAsyncScope();
        return (accountId, (await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(a => a.Id == accountId, Ct)).EncryptedSecret);
    }

    private static async Task<string> ReadSecretAsync(MailnixAppFactory app, Guid accountId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.SingleAsync(a => a.Id == accountId, Ct);
        return scope.ServiceProvider.GetRequiredService<MailSecretProtector>().Unprotect(account.EncryptedSecret);
    }

    [Fact]
    public void Master_key_encryption_round_trips_and_detects_tampering_and_wrong_keys()
    {
        var key = MasterKey.Parse(Key);
        var element = new XElement("masterKey", new XElement("value", "c2VjcmV0"));
        var encrypted = new MasterKeyXmlEncryptor(key).Encrypt(element).EncryptedElement;
        encrypted.ToString().ShouldNotContain("c2VjcmV0");

        var services = new ServiceCollection().AddSingleton(key).BuildServiceProvider();
        new MasterKeyXmlDecryptor(services).Decrypt(encrypted).ToString().ShouldBe(element.ToString());

        var tampered = new XElement(encrypted);
        var value = Convert.FromBase64String((string)tampered.Element("value")!);
        value[0] ^= 1;
        tampered.Element("value")!.Value = Convert.ToBase64String(value);
        Should.Throw<CryptographicException>(() => new MasterKeyXmlDecryptor(services).Decrypt(tampered));

        var other = new ServiceCollection().AddSingleton(MasterKey.Parse(MasterKey.Generate())).BuildServiceProvider();
        Should.Throw<CryptographicException>(() => new MasterKeyXmlDecryptor(other).Decrypt(encrypted));
        Should.Throw<CryptographicException>(() => new MasterKeyXmlDecryptor(new ServiceCollection().BuildServiceProvider()).Decrypt(encrypted));

        Should.Throw<InvalidOperationException>(() => MasterKey.Parse("not base64!"));
        Should.Throw<InvalidOperationException>(() => MasterKey.Parse(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public async Task With_a_master_key_new_keys_are_encrypted_with_it()
    {
        await using var app = new MailnixAppFactory().WithSetting("Mailnix:DataProtection:MasterKey", Key);
        var (accountId, _) = await SeedAccountAsync(app);

        var keys = await KeyXmlAsync(app);
        keys.ShouldNotBeEmpty();
        keys.ShouldAllBe(xml => xml.Contains("MasterKeyXmlDecryptor") && !xml.Contains("<masterKey"));
        (await ReadSecretAsync(app, accountId)).ShouldBe("secret");
    }

    [Fact]
    public async Task The_master_key_can_come_from_a_file()
    {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, Key + Environment.NewLine, Ct);
        try
        {
            await using var app = new MailnixAppFactory().WithSetting("Mailnix:DataProtection:MasterKeyFile", file);
            var (accountId, _) = await SeedAccountAsync(app);
            (await KeyXmlAsync(app)).ShouldAllBe(xml => xml.Contains("MasterKeyXmlDecryptor"));
            (await ReadSecretAsync(app, accountId)).ShouldBe("secret");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public Task Unencrypted_keys_are_replaced_when_a_master_key_is_configured() => WithDatabaseAsync(async app =>
    {
        Guid accountId;
        await using (var unprotected = app("None", null))
        {
            (accountId, _) = await SeedAccountAsync(unprotected);
            (await KeyXmlAsync(unprotected)).ShouldAllBe(xml => !xml.Contains("<encryptedSecret"));
        }

        await using var protectedApp = app("Auto", Key);
        protectedApp.CreateApiClient();
        (await KeyXmlAsync(protectedApp)).ShouldAllBe(xml => xml.Contains("MasterKeyXmlDecryptor"));
        (await ReadSecretAsync(protectedApp, accountId)).ShouldBe("secret");
        (await KeyXmlAsync(protectedApp)).Count.ShouldBe(1);
    });

    [Fact]
    public Task Starting_with_a_wrong_or_missing_master_key_fails_instead_of_losing_the_keys() => WithDatabaseAsync(async app =>
    {
        Guid accountId;
        await using (var first = app("Auto", Key))
        {
            (accountId, _) = await SeedAccountAsync(first);
        }

        await using (var wrong = app("Auto", MasterKey.Generate()))
        {
            Should.Throw<InvalidOperationException>(() => wrong.CreateApiClient()).Message.ShouldContain("not the one");
        }

        await using (var missing = app("None", null))
        {
            Should.Throw<InvalidOperationException>(() => missing.CreateApiClient()).Message.ShouldContain("none is configured");
        }

        // The right key still works afterwards: nothing was replaced.
        await using var again = app("Auto", Key);
        (await ReadSecretAsync(again, accountId)).ShouldBe("secret");
    });

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
