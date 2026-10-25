using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Identity;
using Mailnix.Worker;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Team;

internal static class UserRemoval
{
    /// <summary>
    /// Deletes a user with everything they set up (mail accounts, rules, lists, runs, stats); the mailboxes themselves
    /// stay untouched. An organization of a SaaS installation that has nobody left goes along.
    /// </summary>
    public static async Task DeleteAsync(AppUser user, MailnixDbContext db, UserManager<AppUser> users, IWorkerSignals signals, CancellationToken ct)
    {
        // Lists are not tied to the user by a foreign key; accounts take their rules, runs and stats along.
        await db.NamedLists.Where(l => l.UserId == user.Id).ExecuteDeleteAsync(ct);
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not delete user: {string.Join(", ", result.Errors.Select(e => e.Code))}");
        }

        if (user.TenantId != Tenant.DefaultId && !await db.Users.AnyAsync(u => u.TenantId == user.TenantId, ct))
        {
            await db.Tenants.Where(t => t.Id == user.TenantId).ExecuteDeleteAsync(ct);
        }

        signals.AccountsChanged();
    }

    /// <summary>Whether the user is the only admin of an organization that has other members.</summary>
    public static async Task<bool> IsLastTenantAdminAsync(AppUser member, MailnixDbContext db, UserManager<AppUser> users, bool othersMustRemain)
    {
        if (!await users.IsInRoleAsync(member, Roles.TenantAdmin))
        {
            return false;
        }

        if (othersMustRemain && !await db.Users.AnyAsync(u => u.TenantId == member.TenantId && u.Id != member.Id))
        {
            return false;
        }

        var admins = await users.GetUsersInRoleAsync(Roles.TenantAdmin);
        return admins.Count(a => a.TenantId == member.TenantId) <= 1;
    }
}
