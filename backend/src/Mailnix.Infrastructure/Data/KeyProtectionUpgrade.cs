using System.Security.Cryptography;
using Mailnix.Infrastructure.Options;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Infrastructure.Data;

/// <summary>
/// After key protection is switched on, keys created earlier are still stored unencrypted, and mailbox passwords
/// stay encrypted with them. This creates a new (protected) key, re-encrypts the passwords with it and removes the
/// unprotected keys. Users have to sign in again afterwards (their cookies used the old keys).
/// </summary>
public sealed partial class KeyProtectionUpgrade(
    MailnixDbContext db,
    IKeyManager keys,
    MailSecretProtector secrets,
    IOptions<KeyProtectionOptions> options,
    TimeProvider time,
    ILogger<KeyProtectionUpgrade> logger)
{
    /// <summary>Marks a key whose secret was encrypted by an <c>IXmlEncryptor</c> (here: DPAPI).</summary>
    private const string EncryptedMarker = "<encryptedSecret";

    public async Task UpgradeAsync(CancellationToken ct)
    {
        if (options.Value.Resolve() == KeyProtectionMode.None)
        {
            return;
        }

        var unprotected = await db.DataProtectionKeys.Where(k => k.Xml != null && !k.Xml.Contains(EncryptedMarker)).ToListAsync(ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Encryption keys are now protected: re-encrypted {Accounts} mailbox passwords and removed {Keys} unprotected keys. Users have to sign in again")]
    private partial void LogUpgraded(int keys, int accounts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} mailbox passwords could not be re-encrypted; the unprotected keys are kept")]
    private partial void LogKeptUnprotectedKeys(int count);
}
