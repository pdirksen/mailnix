using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mailnix.Api.Hosting;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;

namespace Mailnix.Api.Mcp;

/// <summary>
/// mailnix as an MCP server: Streamable HTTP on /mcp (stateless, so any API process can answer), bearer tokens
/// only. Clients find the OAuth server through the protected-resource metadata the SDK serves.
/// </summary>
internal static class McpSetup
{
    public const string Path = "/mcp";
    public const string CorsPolicy = "mcp";

    private const string Instructions = """
        mailnix sorts the user's email with rules, like imapfilter. Each mail account has rules in priority order; a
        rule has a condition tree and actions. Non-terminal actions (flags, keywords, copies) of all matching rules
        apply; of the terminal actions (move, delete = move to Trash, spam) only the first matching rule's runs.

        Safety: mailnix never deletes mail permanently, and everything rules do can be undone. Rules you create or
        change are drafts: disabled, or a dry run that only records what it would do. The user switches rules live
        in the app; you cannot. Runs that would move more messages than the account's threshold wait for the
        user's confirmation in the app.

        A good way to build a rule: list_accounts and list_rules for context, get_field_catalog for the fields and
        operators, preview_rule on real mail until the matches look right, then create_rule as a dry run, and tell
        the user to review and enable it in mailnix.
        """;

    /// <summary>Tool arguments and results look like the HTTP API's JSON (camelCase, enums as names, rule shapes).</summary>
    private static readonly JsonSerializerOptions ToolJson =
        RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

    public static IServiceCollection AddMailnixMcp(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationHandler.SchemeName,
                options => options.ForwardChallenge = McpAuthenticationDefaults.AuthenticationScheme)
            .AddMcp(options => options.Events.OnResourceMetadataRequest = context =>
            {
                // Per request: the address depends on Mailnix:PublicUrl or the request's host.
                context.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = OAuthServerEndpoints.Resource(context.HttpContext),
                    AuthorizationServers = [OAuthServerEndpoints.Issuer(context.HttpContext)],
                    ScopesSupported = [OAuthServerEndpoints.ReadScope, OAuthServerEndpoints.WriteScope],
                    BearerMethodsSupported = ["header"],
                    ResourceName = "mailnix",
                };
                return Task.CompletedTask;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(McpPolicies.Read, policy => policy.RequireClaim(McpClaims.Scope))
            .AddPolicy(McpPolicies.Write, policy => policy.RequireClaim(McpClaims.Scope, nameof(ApiScope.ReadWrite)));

        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate", "Mcp-Session-Id")));

        services.AddSingleton<McpLimits>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "mailnix", Title = "mailnix", Version = AppVersion.Current };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(http => http.Stateless = true)
            .AddAuthorizationFilters()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, ct) =>
            {
                try
                {
                    return await next(request, ct);
                }
                catch (McpException e) when (e is not McpProtocolException)
                {
                    // Refusals and validation messages from the tools are answers for the assistant, not server errors.
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = e.Message }] };
                }
            }))
            .WithTools<MailnixTools>(ToolJson);
        return services;
    }

    /// <summary>
    /// Browser-based MCP clients may call the MCP endpoint, the OAuth endpoints and the metadata from other sites.
    /// Only there: no cookies are involved, while the cookie API keeps refusing cross-site requests.
    /// </summary>
    public static IApplicationBuilder UseMcpCors(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(Path)
                || context.Request.Path.StartsWithSegments("/oauth2")
                || context.Request.Path.StartsWithSegments("/.well-known"),
            branch => branch.UseCors(CorsPolicy));

    public static IEndpointRouteBuilder MapMailnixMcp(this IEndpointRouteBuilder routes)
    {
        routes.MapMcp(Path).RequireAuthorization(McpPolicies.Read).ExcludeFromDescription();
        routes.MapOAuthServerEndpoints();
        return routes;
    }
}
