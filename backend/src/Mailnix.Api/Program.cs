using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Mailnix.Api.Accounts;
using Mailnix.Api.Assistant;
using Mailnix.Api.Auth;
using Mailnix.Api.Email;
using Mailnix.Api.Hosting;
using Mailnix.Api.Lists;
using Mailnix.Api.Mcp;
using Mailnix.Api.Profile;
using Mailnix.Api.Rules;
using Mailnix.Api.Runs;
using Mailnix.Api.Stats;
using Mailnix.Api.Team;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure;
using Mailnix.Infrastructure.Data;
using Mailnix.Mail;
using Mailnix.Worker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Net.Http.Headers;
using Scalar.AspNetCore;
using Serilog;

if (args.Contains("--generate-master-key"))
{
    // For Mailnix__DataProtection__MasterKey (or a key file): keep it outside the database and back it up separately.
    Console.WriteLine(Mailnix.Infrastructure.Security.MasterKey.Generate());
    return;
}

// A published build (e.g. the standalone Windows app) keeps wwwroot and data/ next to the executable. Start there
// whatever the working directory is (shortcuts, services), so the UI and relative database paths are found.
if (File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")))
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
}

// Deployments with several processes (SaaS) run this once before the others start: migrations, seed data and the
// data protection key ring are then created by one process only.
const string InitializeOnly = "--initialize-only";
var initializeOnly = args.Contains(InitializeOnly);

var builder = WebApplication.CreateBuilder(args.Where(a => a != InitializeOnly).ToArray());
// Settings in the data folder (Mailnix:DataDirectory) outlive updates, which replace everything next to the executable.
builder.Configuration.AddDataDirectorySettings();

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
builder.Services.AddMailnixMail(builder.Configuration);
builder.Services.AddMailnixAuth(builder.Configuration);
builder.Services.AddMailnixRateLimiting(builder.Configuration);
builder.Services.AddMailnixMcp();
builder.Services.ConfigureHttpJsonOptions(json =>
{
    RuleJson.Configure(json.SerializerOptions);
    json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddScoped<RuleService>();
builder.Services.AddSingleton<MessageSampler>();
builder.Services.AddSingleton<PublicLinks>();
builder.Services.AddSingleton<EmailOutbox>();
builder.Services.AddSingleton<AccountEmails>();
if (hostOptions.ServesApi)
{
    builder.Services.AddSignalR().AddJsonProtocol(json => RuleJson.Configure(json.PayloadSerializerOptions));
    if (!isOpenApiGeneration)
    {
        builder.Services.AddHostedService<EmailDispatcher>();
    }
}

// Live events: straight to the hub in one process; through the database between worker and API processes.
switch (hostOptions.Mode)
{
    case MailnixMode.AllInOne:
        builder.Services.AddSingleton<ILiveNotifier, HubLiveNotifier>();
        break;
    case MailnixMode.Worker:
        builder.Services.AddSingleton<ILiveNotifier, OutboxLiveNotifier>();
        break;
    case MailnixMode.Api when !isOpenApiGeneration:
        builder.Services.AddHostedService<LiveEventRelay>();
        break;
}

if (hostOptions.StopWhenInputCloses)
{
    builder.Services.AddHostedService(services => new InputClosedShutdown(
        Console.In, services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<ILogger<InputClosedShutdown>>()));
}

builder.Services.AddMailnixWorker(builder.Configuration, runWatchers: hostOptions.Mode is MailnixMode.AllInOne or MailnixMode.Worker && !isOpenApiGeneration);
builder.Services.AddMemoryCache();
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<MailnixDbContext>();

var app = builder.Build();
Log.Information("mailnix {Version} ({Mode})", AppVersion.Current, hostOptions.Mode);

if (!isOpenApiGeneration)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    if (initializeOnly)
    {
        // Creates the first key if there is none yet, so all processes share it from the start.
        scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("mailnix.initialize").Protect("ready"u8.ToArray());
        Log.Information("Database and keys are ready");
        return;
    }
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseMiddleware<ClientAbortMiddleware>();

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

app.UseMcpCors();
app.UseAuthentication();
app.UseMiddleware<TenantScopeMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
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
    api.MapAccountEndpoints();
    api.MapOAuthEndpoints();
    api.MapRuleEndpoints();
    api.MapListEndpoints();
    api.MapPreviewEndpoints();
    api.MapAssistantEndpoints();
    api.MapRunEndpoints();
    api.MapStatsEndpoints();
    api.MapActivityEndpoints();
    api.MapTeamEndpoints();
    api.MapSystemEndpoints();
    api.MapProfileEndpoints();
    api.MapApiTokenEndpoints();
    api.MapConnectEndpoints();
    app.MapHub<LiveHub>("/hubs/live");
    app.MapMailnixMcp();
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
