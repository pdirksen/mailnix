using System.Globalization;
using System.Net;
using System.Text;
using Mailnix.Mail.Email;

namespace Mailnix.Api.Email;

/// <summary>The emails mailnix sends, in English and German: plain text plus a simple HTML version.</summary>
internal static class EmailTemplates
{
    /// <summary>A paragraph of an email; `Link` renders as a button in HTML and as a bare URL in text.</summary>
    private sealed record Part(string Text, bool IsLink = false, string? LinkLabel = null);

    private static bool German(string? language) => language == "de";

    public static EmailMessage PasswordReset(string to, string language, string link, TimeSpan validFor) => German(language)
        ? Compose(to, "Dein mailnix-Passwort zurücksetzen", "Passwort zurücksetzen",
            new($"Jemand (hoffentlich du) möchte das Passwort deines mailnix-Kontos {to} zurücksetzen. Öffne diesen Link innerhalb von {Hours(validFor)} Stunden, um ein neues Passwort zu wählen:"),
            new(link, IsLink: true, "Neues Passwort wählen"),
            new("Wenn du das nicht warst, kannst du diese E-Mail ignorieren. Dein Passwort bleibt dann unverändert."))
        : Compose(to, "Reset your mailnix password", "Reset your password",
            new($"Someone (hopefully you) asked to reset the password of your mailnix account {to}. Open this link within {Hours(validFor)} hours to choose a new password:"),
            new(link, IsLink: true, "Choose a new password"),
            new("If this wasn't you, you can ignore this email. Your password stays as it is."));

    public static EmailMessage PasswordChanged(string to, string language, string when, string? resetLink) => German(language)
        ? Compose(to, "Dein mailnix-Passwort wurde geändert", "Passwort geändert",
            [
                new($"Das Passwort deines mailnix-Kontos {to} wurde am {when} geändert. Andere Geräte wurden abgemeldet."),
                .. Suspicious(language, resetLink),
            ])
        : Compose(to, "Your mailnix password was changed", "Password changed",
            [
                new($"The password of your mailnix account {to} was changed on {when}. Other devices were signed out."),
                .. Suspicious(language, resetLink),
            ]);

    public static EmailMessage TwoFactorChanged(string to, string language, bool enabled, string when, string? resetLink) => German(language)
        ? Compose(to, enabled ? "Anmeldung in zwei Schritten ist an" : "Anmeldung in zwei Schritten ist aus", "Anmeldung in zwei Schritten",
            [
                new(enabled
                    ? $"Für dein mailnix-Konto {to} wurde am {when} die Anmeldung in zwei Schritten eingeschaltet. Die Anmeldung fragt jetzt nach einem Code aus deiner Authenticator-App."
                    : $"Für dein mailnix-Konto {to} wurde am {when} die Anmeldung in zwei Schritten ausgeschaltet. Zur Anmeldung genügt jetzt dein Passwort."),
                .. Suspicious(language, resetLink),
            ])
        : Compose(to, enabled ? "Two-factor sign-in is on" : "Two-factor sign-in is off", "Two-factor sign-in",
            [
                new(enabled
                    ? $"Two-factor sign-in was turned on for your mailnix account {to} on {when}. Signing in now asks for a code from your authenticator app."
                    : $"Two-factor sign-in was turned off for your mailnix account {to} on {when}. Signing in now only needs your password."),
                .. Suspicious(language, resetLink),
            ]);

    public static EmailMessage Invitation(string to, string language, string inviter, string organization, string link, string validUntil) => German(language)
        ? Compose(to, $"Einladung zu {organization} auf mailnix", $"Willkommen bei {organization}",
            new($"{inviter} lädt dich ein, {organization} auf mailnix beizutreten. mailnix sortiert eingehende E-Mails mit Regeln, die du grafisch erstellst."),
            new(link, IsLink: true, "Einladung annehmen"),
            new($"Der Link gilt bis {validUntil} und kann einmal verwendet werden."))
        : Compose(to, $"Join {organization} on mailnix", $"Welcome to {organization}",
            new($"{inviter} invites you to join {organization} on mailnix. mailnix sorts incoming mail with rules you build visually."),
            new(link, IsLink: true, "Accept the invitation"),
            new($"The link is valid until {validUntil} and can be used once."));

    public static EmailMessage Test(string to, string language) => German(language)
        ? Compose(to, "Test-E-Mail von mailnix", "Es funktioniert",
            new Part("Wenn du das lesen kannst, kann mailnix E-Mails verschicken: Links zum Zurücksetzen von Passwörtern, Einladungen und Sicherheitshinweise kommen an."))
        : Compose(to, "Test email from mailnix", "It works",
            new Part("If you can read this, mailnix can send email: password reset links, invitations and security notices will arrive."));

    private static Part[] Suspicious(string language, string? resetLink) => resetLink is null
        ? [new(German(language) ? "Warst du das nicht? Wende dich sofort an deinen Administrator." : "Wasn't you? Contact your administrator right away.")]
        :
        [
            new(German(language)
                ? "Warst du das nicht? Setze dein Passwort sofort zurück:"
                : "Wasn't you? Reset your password right away:"),
            new(resetLink, IsLink: true, German(language) ? "Passwort zurücksetzen" : "Reset password"),
        ];

    private static string Hours(TimeSpan span) => ((int)Math.Round(span.TotalHours)).ToString(CultureInfo.InvariantCulture);

    private static EmailMessage Compose(string to, string subject, string heading, params Part[] parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(part.Text).Append("\n\n");
        }

        text.Append("– mailnix\n");

        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html><body style="margin:0;padding:24px;background:#f5f5f5;font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#171717">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0"><tr><td align="center">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:520px;background:#ffffff;border:1px solid #e5e5e5;border-radius:12px">
            <tr><td style="padding:20px 28px;border-bottom:3px solid #eda100;font-size:18px;font-weight:600">mailnix</td></tr>
            <tr><td style="padding:24px 28px 8px">
            """);
        html.Append(CultureInfo.InvariantCulture, $"<h1 style=\"margin:0 0 16px;font-size:20px\">{Encode(heading)}</h1>");
        foreach (var part in parts)
        {
            html.Append(part.IsLink
                ? $"<p style=\"margin:20px 0\"><a href=\"{Encode(part.Text)}\" style=\"display:inline-block;padding:10px 18px;background:#171717;color:#ffffff;border-radius:8px;text-decoration:none;font-weight:600\">{Encode(part.LinkLabel ?? part.Text)}</a></p><p style=\"margin:0 0 16px;font-size:12px;color:#737373;word-break:break-all\">{Encode(part.Text)}</p>"
                : $"<p style=\"margin:0 0 16px;line-height:1.5\">{Encode(part.Text)}</p>");
        }

        html.Append("</td></tr></table></td></tr></table></body></html>");
        return new EmailMessage(to, subject, text.ToString(), html.ToString());
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
