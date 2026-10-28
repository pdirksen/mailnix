using System.Text.Json;

namespace Mailnix.Desktop;

/// <summary>What the desktop app reads from appsettings.json in the data folder (the server reads the same file).</summary>
/// <param name="UpdateUrl">Mailnix:Desktop:UpdateUrl, the update feed (overrides the one the release was built with).</param>
internal sealed record DesktopSettings(int Port, string? UpdateUrl = null)
{
    public const int DefaultPort = 5480;

    /// <summary>Mailnix:Desktop:Port; the default when the file, the key or a valid port is missing.</summary>
    public static DesktopSettings Read(string settingsFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var desktop = Find(Find(document.RootElement, "Mailnix"), "Desktop");
            var port = Find(desktop, "Port");
            var updateUrl = Find(desktop, "UpdateUrl") is { ValueKind: JsonValueKind.String } url ? url.GetString() : null;
            return port is { ValueKind: JsonValueKind.Number } && port.Value.TryGetInt32(out var value) && value is > 0 and <= 65535
                ? new DesktopSettings(value, updateUrl)
                : new DesktopSettings(DefaultPort, updateUrl);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopSettings(DefaultPort);
        }
    }

    // Configuration keys are case-insensitive, JSON property names are not.
    private static JsonElement? Find(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
