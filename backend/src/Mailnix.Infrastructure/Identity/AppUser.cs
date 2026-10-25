using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;

namespace Mailnix.Infrastructure.Identity;

public class AppUser : IdentityUser<Guid>
{
    public Guid TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>IANA time zone id; used for date-based folder templates and stats day boundaries.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>UI language chosen by the user; null until they choose one (the browser's language applies).</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Further addresses of the user (aliases, forwarding targets) besides the sign-in email and the mailbox user
    /// names; for conditions like "I am only in Cc", "sent to my alias" and plus-tags. Lowercase.
    /// </summary>
    public List<string> Addresses { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

public class AppRole : IdentityRole<Guid>
{
    public AppRole()
    {
    }

    public AppRole(string roleName)
        : base(roleName)
    {
    }
}

public static class Roles
{
    /// <summary>Runs the installation: system overview, all tenants.</summary>
    public const string Admin = "Admin";

    /// <summary>Manages the users of their own tenant (invitations).</summary>
    public const string TenantAdmin = "TenantAdmin";

    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Admin, TenantAdmin, User];
}
