namespace Mailnix.Desktop;

/// <param name="AppDirectory">The folder of mailnix-desktop.exe and mailnix.exe.</param>
/// <param name="DataDirectory">Database and settings (Mailnix:DataDirectory of the server).</param>
/// <param name="Installed">Installed with the Windows installer (Velopack), rather than unpacked from a folder.</param>
internal sealed record DesktopPaths(string AppDirectory, string DataDirectory, bool Installed)
{
    public string ServerExecutable => Path.Combine(AppDirectory, "mailnix.exe");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string ServerLog => Path.Combine(LogDirectory, "mailnix.log");

    public string DesktopLog => Path.Combine(LogDirectory, "desktop.log");

    public string SettingsFile => Path.Combine(DataDirectory, "appsettings.json");

    public static DesktopPaths Detect() => For(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// An installation keeps the app in &lt;root&gt;\current, next to &lt;root&gt;\Update.exe. Updates replace that folder and
    /// uninstalling deletes &lt;root&gt;, so the data lives elsewhere. A folder that was just unpacked keeps data\ next to it.
    /// </summary>
    public static DesktopPaths For(string appDirectory, string localAppData)
    {
        var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        var root = Path.GetDirectoryName(app);
        var installed = string.Equals(Path.GetFileName(app), "current", StringComparison.OrdinalIgnoreCase)
            && root is not null
            && File.Exists(Path.Combine(root, "Update.exe"));
        var data = installed ? Path.Combine(localAppData, "mailnix-data") : Path.Combine(app, "data");
        return new DesktopPaths(app, data, installed);
    }
}
