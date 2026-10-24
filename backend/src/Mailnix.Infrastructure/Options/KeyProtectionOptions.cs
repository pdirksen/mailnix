namespace Mailnix.Infrastructure.Options;

/// <summary>How the encryption keys (which protect stored mailbox passwords) are themselves protected at rest.</summary>
public enum KeyProtectionMode
{
    /// <summary><see cref="WindowsUser"/> on Windows, <see cref="None"/> elsewhere.</summary>
    Auto,

    /// <summary>Keys are stored unencrypted; protect the database (and its backups) instead.</summary>
    None,

    /// <summary>Windows DPAPI for the Windows account mailnix runs as: only that account on this machine can read them.</summary>
    WindowsUser,

    /// <summary>Windows DPAPI for this machine: any account on it can read them (e.g. when running as a service).</summary>
    WindowsMachine,
}

public class KeyProtectionOptions
{
    public const string Section = "Mailnix:DataProtection";

    public KeyProtectionMode KeyProtection { get; set; } = KeyProtectionMode.Auto;

    /// <summary>The effective mode on this platform.</summary>
    public KeyProtectionMode Resolve()
    {
        var mode = KeyProtection == KeyProtectionMode.Auto
            ? OperatingSystem.IsWindows() ? KeyProtectionMode.WindowsUser : KeyProtectionMode.None
            : KeyProtection;
        if (mode is KeyProtectionMode.WindowsUser or KeyProtectionMode.WindowsMachine && !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException($"{Section}:KeyProtection = {mode} is only available on Windows.");
        }

        return mode;
    }
}
