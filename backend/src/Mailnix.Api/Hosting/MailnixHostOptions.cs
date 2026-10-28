namespace Mailnix.Api.Hosting;

public enum MailnixMode
{
    /// <summary>API, SPA and mailbox workers in one process (self-hosted default).</summary>
    AllInOne,

    /// <summary>API and SPA only; mailboxes are processed by separate worker replicas.</summary>
    Api,

    /// <summary>Mailbox workers only; exposes nothing but the health endpoint.</summary>
    Worker,
}

public sealed class MailnixHostOptions
{
    public const string Section = "Mailnix";

    public MailnixMode Mode { get; set; } = MailnixMode.AllInOne;

    /// <summary>
    /// The public address of mailnix (e.g. https://mail.example.com), for links in emails. Without it, emails with
    /// links (password reset, invitations) are not sent.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Stop when standard input closes. The Windows desktop app starts mailnix this way, so closing the pipe stops it
    /// cleanly, and mailnix doesn't outlive a desktop app that ends abruptly.
    /// </summary>
    public bool StopWhenInputCloses { get; set; }

    public bool ServesApi => Mode is MailnixMode.AllInOne or MailnixMode.Api;
}
