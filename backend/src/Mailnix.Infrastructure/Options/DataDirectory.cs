using Microsoft.Extensions.Configuration;

namespace Mailnix.Infrastructure.Options;

/// <summary>
/// The folder with this installation's own files: the SQLite database, and settings that outlive updates
/// (<see cref="SettingsFile"/>). An update replaces everything else next to the executable.
/// </summary>
public static class DataDirectory
{
    public const string Key = "Mailnix:DataDirectory";

    public const string SettingsFile = "appsettings.json";

    /// <summary>
    /// The absolute path. A relative one (default "data") is relative to the working directory, which a published build
    /// sets to the executable's folder.
    /// </summary>
    public static string Resolve(IConfiguration configuration) =>
        Path.GetFullPath(configuration[Key] is { Length: > 0 } directory ? directory : "data");
}
