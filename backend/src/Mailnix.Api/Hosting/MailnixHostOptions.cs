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

    public bool ServesApi => Mode is MailnixMode.AllInOne or MailnixMode.Api;
}
