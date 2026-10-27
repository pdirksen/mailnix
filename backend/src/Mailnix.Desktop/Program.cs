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

        return DesktopApp.Run(args);
    }
}
