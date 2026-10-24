namespace Mailnix.Api.Hosting;

/// <summary>
/// CSRF protection for the cookie-authenticated API: state-changing requests must carry a custom header.
/// Browsers cannot attach custom headers cross-site without a CORS preflight, which this API never allows.
/// Works together with the SameSite=Strict auth cookie.
/// </summary>
internal sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "mailnix";

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && !string.Equals(request.Headers[HeaderName], HeaderValue, StringComparison.Ordinal))
        {
            await TypedResults
                .Problem(statusCode: StatusCodes.Status403Forbidden, title: $"Missing {HeaderName} header.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
