using System.Security.Claims;

namespace Mailnix.Api.Hosting;

internal static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id (Identity puts it in the NameIdentifier claim).</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The request is not authenticated.");
}
