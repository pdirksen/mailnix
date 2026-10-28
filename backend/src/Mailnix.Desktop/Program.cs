using Velopack;

namespace Mailnix.Desktop;

// Not top-level statements: their Program class would clash with the API's in the integration tests.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("mailnix-desktop runs on Windows only; start mailnix itself elsewhere.");
            return 1;
        }

        // First: the installer and updater start the app with special arguments and expect it to exit at once.
        // Not Velopack's own apply-at-start: it stops a running mailnix abruptly. DesktopApp installs a downloaded
        // update itself, once it knows no other instance runs.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ =>
            {
                // Uninstalling: no "start with Windows" entry pointing at a deleted program.
                if (OperatingSystem.IsWindows())
                {
                    Autostart.Set(false);
                }
            })
            .Run();

        return DesktopApp.Run(args);
    }
}
