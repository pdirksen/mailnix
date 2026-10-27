using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Mailnix.Desktop;

/// <summary>Runs mailnix in the background with a tray icon: open in the browser, start with Windows, restart, quit.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopApp
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(30);

    private readonly DesktopPaths _paths;
    private readonly TrayIcon _tray;
    private ServerProcess? _server;
    private State _state = State.Stopped;
    private bool _openWhenReady;

    private enum State
    {
        Starting,
        Running,
        Stopped,
    }

    private DesktopApp(DesktopPaths paths, TrayIcon tray, bool openWhenReady)
    {
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

        DesktopApp? app = null;
        using var tray = new TrayIcon(Strings.Starting, () => app!.Menu());
        app = new DesktopApp(paths, tray, openWhenReady: !background);
        tray.Activated += app.Open;
        tray.BalloonClicked += app.ShowLog;
        tray.CloseRequested += tray.Quit;
        tray.SessionEnding += () => app.StopServerAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        tray.Error += app.LogError;
        instance.OnOpenRequested(() => tray.Invoke(app.Open));

        app.StartServer();
        tray.Run();

        // The icon goes away at once; mailnix may take a moment to finish what it is doing.
        tray.Dispose();
        app.StopServerAsync(StopGrace).GetAwaiter().GetResult();
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
        new(Strings.Restart, Restart, Enabled: _state != State.Starting),
        new(Strings.Quit, _tray.Quit),
    ];

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
                _tray.ShowBalloon(Strings.Stopped, Strings.NotRunning, warning: true);
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
            _tray.ShowBalloon(Strings.PortTakenTitle(taken), Strings.PortTaken(server.Url), warning: true);
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
        _tray.ShowBalloon(Strings.StartFailedTitle, Strings.SeeLog, warning: true);
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
        _tray.ShowBalloon(Strings.StoppedTitle, Strings.SeeLog, warning: true);
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
