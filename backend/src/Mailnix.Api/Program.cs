using System.Globalization;
using System.Reflection;
using Mailnix.Api.Auth;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure;
using Mailnix.Infrastructure.Data;
using Microsoft.Net.Http.Headers;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// The build-time OpenAPI generator boots the app without a database.
var isOpenApiGeneration = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

var hostOptions = builder.Configuration.GetSection(MailnixHostOptions.Section).Get<MailnixHostOptions>() ?? new MailnixHostOptions();
builder.Services.AddOptions<MailnixHostOptions>().Bind(builder.Configuration.GetSection(MailnixHostOptions.Section));

builder.Services.AddMailnixInfrastructure(builder.Configuration, persistDataProtectionKeys: !isOpenApiGeneration);
builder.Services.AddMailnixAuth(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<MailnixDbContext>();

var app = builder.Build();

if (!isOpenApiGeneration)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

if (hostOptions.ServesApi)
{
    // Vite emits content-hashed file names under /assets, so those can be cached forever.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
            {
                ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            }
        },
    });
}

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfHeaderMiddleware>();

app.MapHealthChecks("/healthz").AllowAnonymous();

if (hostOptions.ServesApi)
{
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    var api = app.MapGroup("/api");
    api.MapAuthEndpoints();
    api.MapFallback("{**path}", () => TypedResults.NotFound()).AllowAnonymous();

    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = CacheControlHeaderValue.NoCacheString,
    }).AllowAnonymous();
}
else
{
    app.MapFallback(() => TypedResults.NotFound()).AllowAnonymous();
}

await app.RunAsync();

public partial class Program;
