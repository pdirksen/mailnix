using Mailnix.Infrastructure.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Mailnix.Api.Team;

internal static class TenantLimits
{
    /// <summary>A problem response when the tenant may not add another mail account; null when it may.</summary>
    public static async Task<ProblemHttpResult?> CheckAccountLimitAsync(MailnixDbContext db, Guid tenantId, CancellationToken ct)
    {
        var max = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.MaxAccounts).SingleOrDefaultAsync(ct);
        if (max is null || await db.MailAccounts.CountAsync(a => a.TenantId == tenantId, ct) < max)
        {
            return null;
        }

        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: $"Your organization can connect at most {max} mailboxes.",
            extensions: new Dictionary<string, object?> { ["errorCode"] = "AccountLimitReached", ["limit"] = max });
    }
}
