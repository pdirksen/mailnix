using Mailnix.Infrastructure.Options;
using Microsoft.Extensions.Configuration.Json;

namespace Mailnix.Api.Hosting;

internal static class DataDirectorySettings
{
    /// <summary>
    /// Adds appsettings.json from the data directory, if there is one. It ranks above the appsettings files next to the
    /// executable, which an update replaces, and below environment variables and the command line.
    /// </summary>
    public static void AddDataDirectorySettings(this ConfigurationManager configuration)
    {
        var source = new JsonConfigurationSource
        {
            Path = Path.Combine(DataDirectory.Resolve(configuration), DataDirectory.SettingsFile),
            Optional = true,
        };
        // An absolute path needs its own file provider; the default one only sees the content root.
        source.ResolveFileProvider();

        var sources = configuration.Sources;
        var lastAppSettings = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource { Path: { } path } && Path.GetFileName(path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                lastAppSettings = i;
            }
        }

        sources.Insert(lastAppSettings + 1, source);
    }
}
