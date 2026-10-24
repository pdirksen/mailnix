namespace Mailnix.Api.Hosting;

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // PrimeVue injects theme styles at runtime, hence 'unsafe-inline' for styles only.
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        // The dev-only API reference UI (Scalar) loads its own assets and is exempt.
        if (!context.Request.Path.StartsWithSegments("/scalar"))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
        }

        return next(context);
    }
}
