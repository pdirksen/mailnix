namespace Mailnix.Infrastructure.Options;

public enum TenancyMode
{
    /// <summary>Self-hosted: everybody belongs to one tenant; the first user administers it.</summary>
    SingleTenant,

    /// <summary>SaaS: every signup creates its own tenant; its first user administers it and can invite others.</summary>
    MultiTenant,
}

public class TenancyOptions
{
    public const string Section = "Mailnix:Tenancy";

    public TenancyMode Mode { get; set; } = TenancyMode.SingleTenant;

    /// <summary>Account limit of new tenants (MultiTenant mode); null = no limit.</summary>
    public int? DefaultMaxAccounts { get; set; }
}
