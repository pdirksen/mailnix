using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Mailnix.Desktop;

/// <summary>
/// Runs mailnix in the background with a tray icon: open in the browser, start with Windows, restart, quit, and (when
/// installed) updates.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopApp
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    /// <summary>First automatic update check after the start, then every <see cref="UpdateInterval"/>.</summary>
    private static readonly TimeSpan FirstUpdateCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(12);

    /// <summary>Passed when the app starts again after installing an update.</summary>
    private const string UpdatedArgument = "--updated";

    private readonly DesktopPaths _paths;
    private readonly TrayIcon _tray;
    private readonly DesktopUpdates _updates;
    private ServerProcess? _server;
    private State _state = State.Stopped;
    private bool _openWhenReady;

    /// <summary>What clicking the latest notification does (the log, or installing an update).</summary>
    private Action? _balloonAction;

    /// <summary>Install the downloaded update once mailnix has stopped (set by "Install update").</summary>
    private bool _installUpdate;

    /// <summary>Started again after an update: say so once mailnix runs.</summary>
    private bool _announceUpdate;

    private enum State
    {
        Starting,
        Running,
        Stopped,
    }

    private DesktopApp(DesktopPaths paths, TrayIcon tray, DesktopUpdates updates, bool openWhenReady)
    {
        _updates = updates;
        _paths = paths;
        _tray = tray;
        _openWhenReady = openWhenReady;
    }

    public static int Run(string[] args)
    {
        var background = args.Contains(Autostart.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        var paths = DesktopPaths.Detect();
        Directory.CreateDirectory(paths.LogDirectory);

        using var instance = SingleInstance.TryAcquire(paths.DataDirectory, quiet: background);
        if (instance is null)
        {
            return 0;
        }

        // An update downloaded earlier is installed now: this is the only instance and mailnix has not started yet, so
        // nothing runs from the app folder. Installing exits and starts the new version.
        var updates = new DesktopUpdates(CreateUpdater(paths));
        if (updates.ReadyVersion is not null)
        {
            try
            {
                updates.Install(background ? [Autostart.BackgroundArgument, UpdatedArgument] : [UpdatedArgument]);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Start the installed version as it is; the menu offers the update again.
                File.AppendAllText(paths.DesktopLog, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}");
            }
        }

        DesktopApp? app = null;
        using var tray = new TrayIcon(Strings.Starting, () => app!.Menu());
        app = new DesktopApp(paths, tray, updates, openWhenReady: !background)
        {
            _announceUpdate = args.Contains(UpdatedArgument, StringComparer.OrdinalIgnoreCase),
        };
        tray.Activated += app.Open;
        tray.BalloonClicked += () => (app._balloonAction ?? app.ShowLog)();
        tray.CloseRequested += tray.Quit;
        tray.SessionEnding += () => app.StopServerAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        tray.Error += app.LogError;
        instance.OnOpenRequested(() => tray.Invoke(app.Open));

        app.StartServer();
        using var updateChecks = new Timer(_ => tray.Invoke(() => app.CheckForUpdates(manual: false)), null, FirstUpdateCheck, UpdateInterval);
        tray.Run();

        // The icon goes away at once; mailnix may take a moment to finish what it is doing.
        tray.Dispose();
        app.StopServerAsync(StopGrace).GetAwaiter().GetResult();

        // Only now: the update replaces the files mailnix.exe runs from. Exits and starts the new version.
        if (app._installUpdate)
        {
            app._updates.Install([Autostart.BackgroundArgument, UpdatedArgument]);
        }

        return 0;
    }

    private IReadOnlyList<TrayMenuItem> Menu() =>
    [
        new(Strings.Open, Open, IsDefault: true),
        TrayMenuItem.Separator,
        new(Strings.StartWithWindows, () => Autostart.Set(!Autostart.IsEnabled), Checked: Autostart.IsEnabled),
        new(Strings.OpenDataFolder, () => Shell(_paths.DataDirectory)),
        new(Strings.ShowLog, ShowLog),
        TrayMenuItem.Separator,
        .. UpdateMenu(),
        new(Strings.Restart, Restart, Enabled: _state != State.Starting),
        new(Strings.Quit, _tray.Quit),
    ];

    private IEnumerable<TrayMenuItem> UpdateMenu() => _updates.State switch
    {
        UpdateState.Unavailable => [],
        UpdateState.Ready => [new(Strings.InstallUpdate(_updates.ReadyVersion!), InstallUpdate), TrayMenuItem.Separator],
        UpdateState.Checking => [new(Strings.CheckingForUpdates, null, Enabled: false), TrayMenuItem.Separator],
        _ => [new(Strings.CheckForUpdates(_updates.CurrentVersion!), () => CheckForUpdates(manual: true)), TrayMenuItem.Separator],
    };

    /// <summary>Automatic checks stay quiet unless an update is ready; a manual check always reports.</summary>
    private void CheckForUpdates(bool manual)
    {
        if (_updates.State is UpdateState.Unavailable or UpdateState.Checking || (!manual && _updates.State == UpdateState.Ready))
        {
            return;
        }

        Task.Run(async () =>
        {
            var (outcome, version, error) = await _updates.CheckAsync(CancellationToken.None);
            _tray.Invoke(() =>
            {
                switch (outcome)
                {
                    case UpdateOutcome.Ready:
                        Notify(Strings.UpdateReadyTitle(version!), Strings.UpdateReady, warning: false, InstallUpdate);
                        break;
                    case UpdateOutcome.UpToDate when manual:
                        Notify(Strings.UpToDateTitle, Strings.UpToDate(_updates.CurrentVersion!), warning: false, onClick: null);
                        break;
                    case UpdateOutcome.Failed:
                        LogError(error!);
                        if (manual)
                        {
                            Notify(Strings.UpdateFailedTitle, Strings.SeeLog, warning: true, ShowLog);
                        }

                        break;
                }
            });
        });
    }

    /// <summary>Quits: <see cref="Run"/> stops mailnix and then installs the update, which starts the new version.</summary>
    private void InstallUpdate()
    {
        if (_updates.State == UpdateState.Ready)
        {
            _installUpdate = true;
            _tray.Quit();
        }
    }

    private void Notify(string title, string text, bool warning, Action? onClick)
    {
        _balloonAction = onClick;
        _tray.ShowBalloon(title, text, warning);
    }

    /// <summary>Velopack updates for an installed app with a feed address; null otherwise (e.g. an unpacked folder).</summary>
    private static VelopackUpdater? CreateUpdater(DesktopPaths paths)
    {
        var feed = UpdateFeed.Resolve(DesktopSettings.Read(paths.SettingsFile).UpdateUrl, UpdateFeed.BuiltIn);
        if (!paths.Installed || feed is null)
        {
            return null;
        }

        try
        {
            var updater = new VelopackUpdater(feed);
            return updater.IsInstalled ? updater : null;
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException or IOException)
        {
            return null;
        }
    }

    private void Open()
    {
        switch (_state)
        {
            case State.Running:
                Shell(_server!.Url.ToString());
                break;
            case State.Starting:
                _openWhenReady = true;
                break;
            case State.Stopped:
                Notify(Strings.Stopped, Strings.NotRunning, warning: true, onClick: null);
                break;
        }
    }

    private void StartServer()
    {
        _state = State.Starting;
        _tray.SetTooltip(Strings.Starting);

        var preferred = DesktopSettings.Read(_paths.SettingsFile).Port;
        var port = Ports.IsFree(preferred) ? preferred : Ports.FindFree();
        var server = new ServerProcess(new ServerStartInfo(_paths.ServerExecutable, _paths.DataDirectory, port, _paths.ServerLog));
        server.ExitedUnexpectedly += code => _tray.Invoke(() => OnExited(server, code));
        _server = server;

        Task.Run(async () =>
        {
            try
            {
                await server.StartAsync(CancellationToken.None);
                _tray.Invoke(() => OnReady(server, port == preferred ? null : preferred));
            }
            catch (Exception e)
            {
                LogError(e);
                _tray.Invoke(() => OnStartFailed(server));
            }
        });
    }

    private void OnReady(ServerProcess server, int? takenPort)
    {
        if (server != _server)
        {
            return;
        }

        _state = State.Running;
        _tray.SetTooltip(Strings.Running(server.Url));
        if (takenPort is { } taken)
        {
            Notify(Strings.PortTakenTitle(taken), Strings.PortTaken(server.Url), warning: true, onClick: null);
        }

        if (_announceUpdate && _updates.CurrentVersion is { } version)
        {
            _announceUpdate = false;
            Notify(Strings.UpdatedTitle(version), Strings.Running(server.Url), warning: false, Open);
        }

        if (_openWhenReady)
        {
            _openWhenReady = false;
            Open();
        }
    }

    private void OnStartFailed(ServerProcess server)
    {
        if (server != _server)
        {
            return;
        }

        _state = State.Stopped;
        _openWhenReady = false;
        _tray.SetTooltip(Strings.Stopped);
        Notify(Strings.StartFailedTitle, Strings.SeeLog, warning: true, ShowLog);
    }

    private void OnExited(ServerProcess server, int exitCode)
    {
        // During startup, the failed start reports it.
        if (server != _server || _state != State.Running)
        {
            return;
        }

        LogError(new InvalidOperationException($"mailnix ended with exit code {exitCode}."));
        _state = State.Stopped;
        _tray.SetTooltip(Strings.Stopped);
        Notify(Strings.StoppedTitle, Strings.SeeLog, warning: true, ShowLog);
    }

    private void Restart()
    {
        _state = State.Starting;
        _tray.SetTooltip(Strings.Starting);
        Task.Run(async () =>
        {
            await StopServerAsync(StopGrace);
            _tray.Invoke(StartServer);
        });
    }

    private async Task StopServerAsync(TimeSpan grace)
    {
        if (_server is { } server)
        {
            await server.StopAsync(grace);
            server.Dispose();
        }
    }

    private void ShowLog() => Shell(File.Exists(_paths.ServerLog) ? _paths.ServerLog : _paths.LogDirectory);

    private void LogError(Exception e)
    {
        try
        {
            File.AppendAllText(_paths.DesktopLog, string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {e}{Environment.NewLine}"));
        }
        catch (IOException)
        {
            // Nowhere left to report it.
        }
    }

    private static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
