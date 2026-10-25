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

    public string Language { get; set; } = "en";

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
