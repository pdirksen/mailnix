using System.Security.Cryptography;
using System.Xml.Linq;
using Mailnix.Infrastructure.Options;
using Mailnix.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Infrastructure.Data;

/// <summary>
/// After key protection is switched on (or changed), keys created earlier are still stored unencrypted (or protected
/// the old way), and mailbox passwords stay encrypted with them. This creates a new key protected as configured,
/// re-encrypts the passwords with it and removes the old keys. Users have to sign in again afterwards (their cookies
/// used the old keys). Refuses to start when keys need a master key that is missing or different.
/// </summary>
public sealed partial class KeyProtectionUpgrade(
    MailnixDbContext db,
    IKeyManager keys,
    MailSecretProtector secrets,
    IOptions<KeyProtectionOptions> options,
    IServiceProvider services,
    TimeProvider time,
    ILogger<KeyProtectionUpgrade> logger)
{
    public async Task UpgradeAsync(CancellationToken ct)
    {
        var mode = options.Value.Resolve();
        var stored = await db.DataProtectionKeys.Where(k => k.Xml != null).ToListAsync(ct);
        CheckMasterKey(stored.Select(k => k.Xml!));

        if (mode == KeyProtectionMode.None)
        {
            LogUnprotected();
            return;
        }

        // The decryptor named in a key's XML shows how its secret is protected.
        var marker = mode == KeyProtectionMode.MasterKey ? MasterKeyXml.DecryptorName : "DpapiXmlDecryptor";
        var unprotected = stored.Where(k => !k.Xml!.Contains(marker, StringComparison.Ordinal)).ToList();
        if (unprotected.Count == 0)
        {
            return;
        }

        // The newest active key encrypts from now on; this one is protected by the configured encryptor.
        var now = time.GetUtcNow();
        keys.CreateNewKey(now, now.AddDays(90));

        var accounts = await db.MailAccounts.ToListAsync(ct);
        var failed = 0;
        foreach (var account in accounts)
        {
            try
            {
                account.EncryptedSecret = secrets.Protect(secrets.Unprotect(account.EncryptedSecret));
            }
            catch (CryptographicException)
            {
                failed++;
            }
        }

        await db.SaveChangesAsync(ct);
        if (failed > 0)
        {
            // Keep the old keys: they may still be needed once the cause is fixed.
            LogKeptUnprotectedKeys(failed);
            return;
        }

        db.DataProtectionKeys.RemoveRange(unprotected);
        await db.SaveChangesAsync(ct);
        LogUpgraded(unprotected.Count, accounts.Count);
    }

    /// <summary>Keys encrypted with a master key can only be read with that same key: fail now rather than lose them.</summary>
    private void CheckMasterKey(IEnumerable<string> keyXml)
    {
        var configured = services.GetService(typeof(MasterKey)) as MasterKey;
        foreach (var xml in keyXml.Where(x => x.Contains(MasterKeyXml.DecryptorName, StringComparison.Ordinal)))
        {
            var id = (string?)XElement.Parse(xml).Descendants("encryptedKey").FirstOrDefault()?.Attribute("masterKeyId");
            if (configured is null)
            {
                throw new InvalidOperationException(
                    $"The encryption keys in the database are protected with a master key, but none is configured ({KeyProtectionOptions.Section}:MasterKey or MasterKeyFile).");
            }

            if (id != configured.Id)
            {
                throw new InvalidOperationException(
                    "The configured master key is not the one the encryption keys in the database were protected with. Configure the original master key.");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encryption keys are stored unencrypted in the database: anyone with a copy of it can decrypt the mailbox passwords. Set Mailnix__DataProtection__MasterKey (generate one with --generate-master-key) or MasterKeyFile to protect them")]
    private partial void LogUnprotected();

    [LoggerMessage(Level = LogLevel.Information, Message = "Encryption keys are now protected: re-encrypted {Accounts} mailbox passwords and removed {Keys} unprotected keys. Users have to sign in again")]
    private partial void LogUpgraded(int keys, int accounts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} mailbox passwords could not be re-encrypted; the unprotected keys are kept")]
    private partial void LogKeptUnprotectedKeys(int count);
}
