using Mailnix.Infrastructure.Security;

namespace Mailnix.Infrastructure.Options;

/// <summary>How the encryption keys (which protect stored mailbox passwords) are themselves protected at rest.</summary>
public enum KeyProtectionMode
{
    /// <summary>
    /// <see cref="MasterKey"/> when a master key is configured, otherwise <see cref="WindowsUser"/> on Windows and
    /// <see cref="None"/> elsewhere.
    /// </summary>
    Auto,

    /// <summary>Keys are stored unencrypted; protect the database (and its backups) instead.</summary>
    None,

    /// <summary>Windows DPAPI for the Windows account mailnix runs as: only that account on this machine can read them.</summary>
    WindowsUser,

    /// <summary>Windows DPAPI for this machine: any account on it can read them (e.g. when running as a service).</summary>
    WindowsMachine,

    /// <summary>Encrypted with a master key kept outside the database (<see cref="KeyProtectionOptions.MasterKey"/> or a file).</summary>
    MasterKey,
}

public class KeyProtectionOptions
{
    public const string Section = "Mailnix:DataProtection";

    public KeyProtectionMode KeyProtection { get; set; } = KeyProtectionMode.Auto;

    /// <summary>Base64 master key, e.g. from an environment variable. Generate one with <c>mailnix --generate-master-key</c>.</summary>
    public string? MasterKey { get; set; }

    /// <summary>File containing the base64 master key, e.g. a Docker secret (<c>/run/secrets/mailnix_master_key</c>).</summary>
    public string? MasterKeyFile { get; set; }

    private bool HasMasterKey => !string.IsNullOrWhiteSpace(MasterKey) || !string.IsNullOrWhiteSpace(MasterKeyFile);

    /// <summary>The effective mode on this platform.</summary>
    public KeyProtectionMode Resolve()
    {
        var mode = KeyProtection switch
        {
            KeyProtectionMode.Auto when HasMasterKey => KeyProtectionMode.MasterKey,
            KeyProtectionMode.Auto => OperatingSystem.IsWindows() ? KeyProtectionMode.WindowsUser : KeyProtectionMode.None,
            _ => KeyProtection,
        };

        if (mode is KeyProtectionMode.WindowsUser or KeyProtectionMode.WindowsMachine && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException($"{Section}:KeyProtection = {mode} is only available on Windows.");
        }

        if (mode == KeyProtectionMode.MasterKey && !HasMasterKey)
        {
            throw new InvalidOperationException($"{Section}:KeyProtection = MasterKey needs {Section}:MasterKey or {Section}:MasterKeyFile.");
        }

        return mode;
    }

    /// <summary>The configured master key (from the value or the file); null when none is configured.</summary>
    public Security.MasterKey? LoadMasterKey()
    {
        if (!string.IsNullOrWhiteSpace(MasterKey) && !string.IsNullOrWhiteSpace(MasterKeyFile))
        {
            throw new InvalidOperationException($"Set either {Section}:MasterKey or {Section}:MasterKeyFile, not both.");
        }

        if (!string.IsNullOrWhiteSpace(MasterKeyFile))
        {
            if (!File.Exists(MasterKeyFile))
            {
                throw new InvalidOperationException($"The master key file '{MasterKeyFile}' does not exist.");
            }

            return Security.MasterKey.Parse(File.ReadAllText(MasterKeyFile));
        }

        return string.IsNullOrWhiteSpace(MasterKey) ? null : Security.MasterKey.Parse(MasterKey);
    }
}
