using System.Reflection;

namespace Mailnix.Api.Hosting;

/// <summary>The version this build was published with; see Version in Directory.Build.props.</summary>
internal static class AppVersion
{
    /// <summary>E.g. "1.4.0", or "0.0.0-dev+1a2b3c4" when the build knew its commit.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";
        // The SDK appends the full commit hash; the short form is enough to find it.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }
}
