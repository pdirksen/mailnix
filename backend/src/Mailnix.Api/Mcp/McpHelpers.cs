using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Mailnix.Api.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using ModelContextProtocol;

namespace Mailnix.Api.Mcp;

/// <summary>Turns an endpoint handler's result into the tool's value, or into an error the assistant can read.</summary>
internal static class McpResults
{
    public static T Value<T>(IResult result) => result switch
    {
        INestedHttpResult nested => Value<T>(nested.Result),
        IValueHttpResult<T> { Value: { } value } => value,
        NotFound => throw new McpException("Not found."),
        ValidationProblem invalid => throw new McpException(
            "Invalid: " + string.Join("; ", invalid.ProblemDetails.Errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))),
        ProblemHttpResult problem => throw new McpException(
            string.Join(" ", new[] { problem.ProblemDetails.Title, problem.ProblemDetails.Detail }.Where(s => !string.IsNullOrEmpty(s)))),
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}."),
    };
}

/// <summary>The same data-annotation checks the HTTP endpoints apply to their request bodies.</summary>
/// <remarks>
/// Uses the endpoints' generated validation (ASP0029 marks its direct use as subject to change): DataAnnotations'
/// own Validator does not see attributes on record constructor parameters.
/// </remarks>
#pragma warning disable ASP0029
internal static class McpValidation
{
    public static async Task ValidateAsync<T>(T value, IServiceProvider services, CancellationToken ct)
        where T : notnull
    {
        var options = services.GetRequiredService<IOptions<ValidationOptions>>().Value;
        if (!options.TryGetValidatableTypeInfo(typeof(T), out var info))
        {
            return;
        }

        var context = new ValidateContext { ValidationOptions = options, ValidationContext = new ValidationContext(value, services, null) };
        await info.ValidateAsync(value, context, ct);
        if (context.ValidationErrors is { Count: > 0 } errors)
        {
            throw new McpException("Invalid: " + string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}")));
        }
    }
}
#pragma warning restore ASP0029

/// <summary>
/// Limits for tools that reach the mail server, per user, like the matching HTTP endpoints have (the HTTP rate
/// limiter only sees the MCP endpoint as a whole).
/// </summary>
internal sealed class McpLimits(IOptions<RateLimitOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _mailConnections = PerUser(options.Value.MailConnectionsPerMinute);
    private readonly PartitionedRateLimiter<string> _previews = PerUser(options.Value.PreviewsPerMinute);

    public void MailConnection(ClaimsPrincipal user) => Acquire(_mailConnections, user);

    public void Preview(ClaimsPrincipal user) => Acquire(_previews, user);

    public void Dispose()
    {
        _mailConnections.Dispose();
        _previews.Dispose();
    }

    private static void Acquire(PartitionedRateLimiter<string> limiter, ClaimsPrincipal user)
    {
        using var lease = limiter.AttemptAcquire(user.GetUserId().ToString());
        if (!lease.IsAcquired)
        {
            throw new McpException("Too many requests to the mail server. Try again in a minute.");
        }
    }

    private static PartitionedRateLimiter<string> PerUser(int perMinute) =>
        PartitionedRateLimiter.Create<string, string>(user => RateLimitPartition.GetFixedWindowLimiter(
            user,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
}
