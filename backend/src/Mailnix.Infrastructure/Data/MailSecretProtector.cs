using Microsoft.AspNetCore.DataProtection;

namespace Mailnix.Infrastructure.Data;

/// <summary>Encrypts mailbox credentials at rest. Keys live in the database (see InfrastructureServiceCollectionExtensions).</summary>
public sealed class MailSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Mailnix.MailAccount.Secret.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string encrypted) => _protector.Unprotect(encrypted);
}
