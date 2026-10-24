using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Mailnix.Api.Hosting;

public sealed class RateLimitOptions
{
    public const string Section = "Mailnix:RateLimits";

    /// <summary>Requests per user and minute to endpoints that open IMAP connections (tests, folder listings, saves).</summary>
    public int MailConnectionsPerMinute { get; set; } = 20;

    /// <summary>Rule previews per user and minute (most are served from the message cache).</summary>
    public int PreviewsPerMinute { get; set; } = 60;
}

internal static class RateLimiting
{
    public const string MailConnections = "mail-connections";
    public const string Previews = "previews";

    public static IServiceCollection AddMailnixRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(MailConnections, context => PerUser(context, options.MailConnectionsPerMinute));
            limiter.AddPolicy(Previews, context => PerUser(context, options.PreviewsPerMinute));
        });
        return services;
    }

    private static RateLimitPartition<string> PerUser(HttpContext context, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
