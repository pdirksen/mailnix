namespace Mailnix.Api.Hosting;

/// <summary>
/// Requests the client gave up on (navigated away, closed the tab) end with an OperationCanceledException.
/// That is not a server error: answer 499 (client closed request) instead of logging a 500.
/// </summary>
internal sealed partial class ClientAbortMiddleware(RequestDelegate next, ILogger<ClientAbortMiddleware> logger)
{
    public const int ClientClosedRequest = 499;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            LogAborted(context.Request.Method, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ClientClosedRequest;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Method} {Path} was cancelled by the client")]
    private partial void LogAborted(string method, PathString path);
}
