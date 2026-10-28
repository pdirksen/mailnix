using System.Reflection;
using Velopack;

namespace Mailnix.Desktop;

/// <summary>Where updates come from and how they are installed; Velopack in the app, a fake in tests.</summary>
internal interface IUpdater
{
    /// <summary>The installed version, e.g. "1.4.0".</summary>
    string CurrentVersion { get; }

    /// <summary>A version downloaded earlier and waiting to be installed, if any.</summary>
    string? PendingVersion { get; }

    /// <summary>Looks for a newer version and downloads it. Returns its version, or null when this one is the newest.</summary>
    Task<string?> CheckAndDownloadAsync(CancellationToken ct);

    /// <summary>Installs the downloaded version and starts the app again with <paramref name="restartArgs"/>; exits this process.</summary>
    void ApplyAndRestart(string[] restartArgs);
}

/// <summary>Updates through Velopack: the feed is a folder of release files on a web server (or a file share).</summary>
internal sealed class VelopackUpdater(string feed) : IUpdater
{
    private readonly UpdateManager _manager = new(feed);
    private UpdateInfo? _downloaded;

    /// <summary>Only an installed app can update itself (not a folder that was just unpacked).</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "?";

    public string? PendingVersion => (_downloaded?.TargetFullRelease ?? _manager.UpdatePendingRestart)?.Version.ToString();

    public async Task<string?> CheckAndDownloadAsync(CancellationToken ct)
    {
        if (await _manager.CheckForUpdatesAsync() is not { } update)
        {
            return null;
        }

        await _manager.DownloadUpdatesAsync(update, progress: null, ct);
        _downloaded = update;
        return update.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart(string[] restartArgs) =>
        _manager.ApplyUpdatesAndRestart(_downloaded?.TargetFullRelease ?? _manager.UpdatePendingRestart, restartArgs);
}

/// <summary>The address of the update feed: the data folder's settings win over the one the release was built with.</summary>
internal static class UpdateFeed
{
    /// <summary>Set at build time (MSBuild property MailnixUpdateUrl, see scripts/release.ps1).</summary>
    public static string? BuiltIn =>
        typeof(UpdateFeed).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MailnixUpdateUrl")?.Value;

    /// <summary>The feed to use, or null when updates are not set up.</summary>
    public static string? Resolve(string? configured, string? builtIn) =>
        new[] { configured, builtIn }.Select(u => u?.Trim()).FirstOrDefault(u => !string.IsNullOrEmpty(u));
}

/// <summary>What the update side of the tray app is doing, for its menu and notifications.</summary>
internal enum UpdateState
{
    /// <summary>Not an installed app, or no feed configured.</summary>
    Unavailable,
    Idle,
    Checking,

    /// <summary>A newer version is downloaded and can be installed.</summary>
    Ready,
}

/// <summary>What a check found, for the notification after a manual check.</summary>
internal enum UpdateOutcome
{
    UpToDate,
    Ready,
    Failed,
}

/// <summary>
/// Checks for updates now and then (and when asked), downloads them in the background and installs them on request.
/// The tray app decides how to show the results; this class only keeps the state.
/// </summary>
internal sealed class DesktopUpdates(IUpdater? updater)
{
    private int _checking;

    public UpdateState State => updater is null ? UpdateState.Unavailable
        : Volatile.Read(ref _checking) == 1 ? UpdateState.Checking
        : ReadyVersion is not null ? UpdateState.Ready
        : UpdateState.Idle;

    public string? CurrentVersion => updater?.CurrentVersion;

    /// <summary>The downloaded version waiting to be installed.</summary>
    public string? ReadyVersion { get; private set; } = updater?.PendingVersion;

    /// <summary>Checks once; a check that is already running makes this one return at once (as <see cref="UpdateOutcome.UpToDate"/>).</summary>
    public async Task<(UpdateOutcome Outcome, string? Version, Exception? Error)> CheckAsync(CancellationToken ct)
    {
        if (updater is null || Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return (UpdateOutcome.UpToDate, ReadyVersion, null);
        }

        try
        {
            var version = await updater.CheckAndDownloadAsync(ct);
            if (version is null)
            {
                return (ReadyVersion is null ? UpdateOutcome.UpToDate : UpdateOutcome.Ready, ReadyVersion, null);
            }

            ReadyVersion = version;
            return (UpdateOutcome.Ready, version, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (UpdateOutcome.Failed, null, e);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Call only after mailnix has stopped: the update replaces the files it runs from.</summary>
    public void Install(string[] restartArgs)
    {
        if (updater is not null && ReadyVersion is not null)
        {
            updater.ApplyAndRestart(restartArgs);
        }
    }
}
