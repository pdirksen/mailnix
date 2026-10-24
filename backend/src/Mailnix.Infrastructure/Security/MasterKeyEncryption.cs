using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Infrastructure.Security;

/// <summary>
/// A secret kept outside the database (environment variable or file, e.g. a Docker secret) that encrypts the data
/// protection keys stored in it. Without it, a copy of the database or a backup cannot decrypt mailbox passwords.
/// </summary>
public sealed class MasterKey
{
    public const int Length = 32;

    private static readonly byte[] IdInfo = "mailnix master key id"u8.ToArray();

    private MasterKey(byte[] bytes)
    {
        EncryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, info: "mailnix key encryption v1: aes"u8.ToArray());
        MacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, bytes, 32, info: "mailnix key encryption v1: hmac"u8.ToArray());
        Id = Convert.ToHexStringLower(HMACSHA256.HashData(bytes, IdInfo)[..8]);
    }

    /// <summary>Identifies the key without revealing it, so a wrong master key is recognized.</summary>
    public string Id { get; }

    internal byte[] EncryptionKey { get; }

    internal byte[] MacKey { get; }

    /// <summary>A new random master key, base64-encoded.</summary>
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(Length));

    /// <summary>Parses a base64-encoded master key of at least 32 bytes.</summary>
    public static MasterKey Parse(string value)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The master key is not valid base64. Generate one with: mailnix --generate-master-key");
        }

        if (bytes.Length < Length)
        {
            throw new InvalidOperationException($"The master key must be at least {Length} bytes. Generate one with: mailnix --generate-master-key");
        }

        return new MasterKey(bytes);
    }
}

/// <summary>Encrypts data protection key XML with the master key (AES-256-CBC, then HMAC-SHA256 over everything).</summary>
public sealed class MasterKeyXmlEncryptor(MasterKey key) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key.EncryptionKey;
        var ciphertext = aes.EncryptCbc(plaintext, iv);
        var tag = MasterKeyXml.Tag(key, iv, ciphertext);

        var element = new XElement(
            "encryptedKey",
            new XComment(" This key is encrypted with the mailnix master key. "),
            new XAttribute("version", 1),
            new XAttribute("masterKeyId", key.Id),
            new XElement("iv", Convert.ToBase64String(iv)),
            new XElement("value", Convert.ToBase64String(ciphertext)),
            new XElement("tag", Convert.ToBase64String(tag)));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Created by the data protection system for keys written by <see cref="MasterKeyXmlEncryptor"/>.</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = services.GetService<MasterKey>()
            ?? throw new CryptographicException("This key was encrypted with a mailnix master key, but none is configured (Mailnix:DataProtection:MasterKey or MasterKeyFile).");
        if ((string?)encryptedElement.Attribute("masterKeyId") != key.Id)
        {
            throw new CryptographicException("This key was encrypted with a different mailnix master key than the one configured.");
        }

        var iv = Convert.FromBase64String((string)encryptedElement.Element("iv")!);
        var ciphertext = Convert.FromBase64String((string)encryptedElement.Element("value")!);
        var tag = Convert.FromBase64String((string)encryptedElement.Element("tag")!);
        if (!CryptographicOperations.FixedTimeEquals(tag, MasterKeyXml.Tag(key, iv, ciphertext)))
        {
            throw new CryptographicException("The encrypted key was modified.");
        }

        using var aes = Aes.Create();
        aes.Key = key.EncryptionKey;
        return XElement.Parse(Encoding.UTF8.GetString(aes.DecryptCbc(ciphertext, iv)));
    }
}

internal static class MasterKeyXml
{
    /// <summary>Marker of keys encrypted this way, as written into the stored key XML.</summary>
    public const string DecryptorName = nameof(MasterKeyXmlDecryptor);

    public static byte[] Tag(MasterKey key, byte[] iv, byte[] ciphertext)
    {
        using var hmac = new HMACSHA256(key.MacKey);
        hmac.TransformBlock([1], 0, 1, null, 0); // version
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);
        hmac.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return hmac.Hash!;
    }
}
