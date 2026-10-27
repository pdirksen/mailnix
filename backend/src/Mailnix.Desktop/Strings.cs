using System.Globalization;

namespace Mailnix.Desktop;

/// <summary>The desktop app's texts in the Windows display language: German or English, like the web UI.</summary>
internal static class Strings
{
    private static readonly bool German = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public static string Open => German ? "mailnix öffnen" : "Open mailnix";

    public static string StartWithWindows => German ? "Mit Windows starten" : "Start with Windows";

    public static string OpenDataFolder => German ? "Datenordner öffnen" : "Open data folder";

    public static string ShowLog => German ? "Protokoll anzeigen" : "Show log";

    public static string Restart => German ? "Neu starten" : "Restart";

    public static string Quit => German ? "Beenden" : "Quit";

    public static string Starting => German ? "mailnix startet …" : "mailnix is starting…";

    public static string Stopped => German ? "mailnix ist beendet" : "mailnix is not running";

    public static string Running(Uri url) => German ? $"mailnix läuft auf {url}" : $"mailnix runs at {url}";

    public static string PortTakenTitle(int port) => German ? $"Port {port} ist belegt" : $"Port {port} is in use";

    public static string PortTaken(Uri url) => German
        ? $"mailnix läuft deshalb auf {url}. Einen festen Port legt Mailnix:Desktop:Port in appsettings.json im Datenordner fest."
        : $"mailnix runs at {url} instead. Set a fixed port with Mailnix:Desktop:Port in appsettings.json in the data folder.";

    public static string StartFailedTitle => German ? "mailnix konnte nicht starten" : "mailnix could not start";

    public static string StoppedTitle => German ? "mailnix wurde unerwartet beendet" : "mailnix stopped unexpectedly";

    public static string SeeLog => German
        ? "Einzelheiten stehen im Protokoll (hier klicken). „Neu starten“ im Menü startet mailnix wieder."
        : "Details are in the log (click here). Restart in the menu starts mailnix again.";

    public static string NotRunning => German ? "mailnix läuft gerade nicht. „Neu starten“ im Menü startet es." : "mailnix is not running. Restart in the menu starts it.";
}
