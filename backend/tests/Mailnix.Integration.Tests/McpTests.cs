using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailnix.Api.Mcp;
using Mailnix.Core.Rules;
using Mailnix.Infrastructure.Data;
using Mailnix.Integration.Tests.Fakes;
using Mailnix.Mail;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Mailnix.Integration.Tests;

public sealed class McpTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = RuleJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static readonly object Newsletters = new { kind = "condition", field = "IsNewsletter", @operator = "IsTrue" };

    private static readonly object[] MoveToNewsletters = [new { type = "MoveTo", folder = "INBOX.Newsletters" }];

    private readonly FakeMessageReader _reader = new();
    private readonly MailnixAppFactory _app;
    private HttpClient _owner = null!;
    private Guid _accountId;

    public McpTests()
    {
        _app = new MailnixAppFactory().WithServices(s => s.Replace(ServiceDescriptor.Singleton<IMessageReader>(_reader)));
        _reader.Folders["INBOX"].AddRange(
        [
            FakeMessageReader.Message(1, "news@shop.example", "Autumn sale", newsletter: true),
            FakeMessageReader.Message(2, "boss@example.com", "Quarterly numbers"),
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _owner = _app.CreateApiClient();
        (await _owner.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        _accountId = await Seed.AccountAsync(_app, "owner@example.com");
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static async Task<string> CreateTokenAsync(HttpClient client, ApiScope scope, string name = "Claude Code")
    {
        var response = await client.PostAsJsonAsync("/api/tokens", new { name, scope = scope.ToString() }, Ct);
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreatedApiTokenResponse>(Json, Ct))!;
        created.Secret.ShouldStartWith("mnx_pat_");
        created.McpUrl.ShouldBe("http://localhost/mcp");
        return created.Secret;
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = _app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        result.IsError.ShouldNotBe(true, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<string> CallFailingAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        result.IsError.ShouldBe(true);
        return result.Content.OfType<TextContentBlock>().Single().Text;
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value is null ? null : (object?)JsonSerializer.SerializeToElement(p.Value, Json));

    [Fact]
    public async Task Without_a_token_the_endpoint_points_to_the_authorization_server()
    {
        var anonymous = _app.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.ShouldContain("Bearer");
        challenge.ShouldContain("resource_metadata=");

        var metadataUrl = challenge.Split("resource_metadata=\"")[1].Split('"')[0];
        var resource = await anonymous.GetFromJsonAsync<JsonElement>(metadataUrl, Ct);
        resource.GetProperty("resource").GetString().ShouldBe("http://localhost/mcp");
        resource.GetProperty("authorization_servers")[0].GetString().ShouldBe("http://localhost");

        var server = await anonymous.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server", Ct);
        server.GetProperty("issuer").GetString().ShouldBe("http://localhost");
        server.GetProperty("token_endpoint").GetString().ShouldBe("http://localhost/oauth2/token");
        server.GetProperty("code_challenge_methods_supported")[0].GetString().ShouldBe("S256");
    }

    [Fact]
    public async Task The_sign_in_cookie_does_not_open_the_mcp_endpoint()
    {
        var response = await _owner.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Nor does a token open the cookie API.
        var token = await CreateTokenAsync(_owner, ApiScope.ReadWrite);
        var bearer = _app.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await bearer.GetAsync("/api/accounts", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_read_token_sees_and_uses_only_the_read_tools()
    {
        await using var client = await ConnectAsync(await CreateTokenAsync(_owner, ApiScope.Read));

        var tools = (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ToList();
        tools.ShouldContain("list_accounts");
        tools.ShouldContain("preview_rule");
        tools.ShouldNotContain("create_rule");
        tools.ShouldNotContain("start_run");

        var accounts = await CallAsync(client, "list_accounts");
        accounts.GetArrayLength().ShouldBe(1);
        accounts[0].GetProperty("id").GetGuid().ShouldBe(_accountId);
        accounts[0].TryGetProperty("encryptedSecret", out _).ShouldBeFalse();

        (await Should.ThrowAsync<McpProtocolException>(() => client.CallToolAsync("create_rule", Args(("accountId", _accountId), ("name", "x")), cancellationToken: Ct).AsTask()))
            .Message.ShouldContain("authorization");
        (await DbAsync(db => db.Rules.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Rules_are_previewed_and_created_as_drafts_marked_with_the_token()
    {
        await using var client = await ConnectAsync(await CreateTokenAsync(_owner, ApiScope.ReadWrite, "Laptop"));

        var catalog = await CallAsync(client, "get_field_catalog");
        catalog.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field").GetString()).ShouldContain("IsNewsletter");

        var preview = await CallAsync(client, "preview_rule", Args(
            ("accountId", _accountId),
            ("conditions", Newsletters),
            ("actions", MoveToNewsletters),
            ("folder", "INBOX")));
        preview.GetProperty("examined").GetInt32().ShouldBe(2);
        preview.GetProperty("matched").GetInt32().ShouldBe(1);
        preview.GetProperty("messages").EnumerateArray().ShouldAllBe(m => m.GetProperty("trace").ValueKind == JsonValueKind.Null);

        var created = await CallAsync(client, "create_rule", Args(
            ("accountId", _accountId),
            ("name", "Newsletters"),
            ("conditions", Newsletters),
            ("actions", MoveToNewsletters),
            ("mode", "DryRun")));
        created.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        created.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        created.GetProperty("origin").GetString().ShouldBe("Laptop");
        var ruleId = created.GetProperty("id").GetGuid();

        // Disabled is the default; the rule stays a draft when changed.
        var updated = await CallAsync(client, "update_rule", Args(
            ("ruleId", ruleId),
            ("name", "Newsletters (all)"),
            ("conditions", Newsletters),
            ("actions", MoveToNewsletters)));
        updated.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        updated.GetProperty("dryRun").GetBoolean().ShouldBeFalse();

        // The user switches it live in the app; then it is out of reach, and the app's change clears the origin.
        var rule = await _owner.GetFromJsonAsync<JsonElement>($"/api/rules/{ruleId}", Ct);
        var live = JsonSerializer.Deserialize<Dictionary<string, object?>>(rule.GetRawText(), Json)!;
        live["enabled"] = true;
        live["dryRun"] = false;
        (await _owner.PutAsJsonAsync($"/api/rules/{ruleId}", live, Ct)).EnsureSuccessStatusCode();
        (await DbAsync(db => db.Rules.SingleAsync(r => r.Id == ruleId, Ct))).Origin.ShouldBeNull();

        var refused = await CallFailingAsync(client, "update_rule", Args(
            ("ruleId", ruleId),
            ("name", "Hijacked"),
            ("conditions", Newsletters),
            ("actions", new object[] { new { type = "Delete" } })));
        refused.ShouldContain("live");
        (await DbAsync(db => db.Rules.SingleAsync(r => r.Id == ruleId, Ct))).Name.ShouldBe("Newsletters (all)");
    }

    [Fact]
    public async Task Invalid_rules_are_explained_to_the_assistant()
    {
        await using var client = await ConnectAsync(await CreateTokenAsync(_owner, ApiScope.ReadWrite));
        var error = await CallFailingAsync(client, "create_rule", Args(
            ("accountId", _accountId),
            ("name", new string('x', 201)),
            ("conditions", Newsletters),
            ("actions", MoveToNewsletters)));
        error.ShouldContain("Invalid");
        error.ShouldContain("Name", Case.Insensitive);
    }

    [Fact]
    public async Task Runs_started_through_mcp_are_queued_like_manual_runs_and_marked()
    {
        await using var client = await ConnectAsync(await CreateTokenAsync(_owner, ApiScope.ReadWrite, "Claude"));
        var run = await CallAsync(client, "start_run", Args(("accountId", _accountId), ("limit", 20)));
        run.GetProperty("state").GetString().ShouldBe("Queued");
        run.GetProperty("trigger").GetString().ShouldBe("Manual");
        run.GetProperty("origin").GetString().ShouldBe("Claude");

        var runs = await CallAsync(client, "list_runs");
        runs[0].GetProperty("origin").GetString().ShouldBe("Claude");

        var cancelled = await CallAsync(client, "cancel_run", Args(("runId", run.GetProperty("id").GetGuid())));
        cancelled.GetProperty("state").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public async Task A_token_only_reaches_its_own_users_data()
    {
        var other = _app.CreateApiClient();
        (await other.RegisterAsync("other@example.com")).EnsureSuccessStatusCode();
        await using var client = await ConnectAsync(await CreateTokenAsync(other, ApiScope.ReadWrite));

        (await CallAsync(client, "list_accounts")).GetArrayLength().ShouldBe(0);
        (await CallFailingAsync(client, "list_folders", Args(("accountId", _accountId)))).ShouldContain("Not found");
        await CallFailingAsync(client, "start_run", Args(("accountId", _accountId)));
        (await DbAsync(db => db.Runs.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Revoked_and_expired_tokens_stop_working()
    {
        var token = await CreateTokenAsync(_owner, ApiScope.Read);
        await using (var client = await ConnectAsync(token))
        {
            await CallAsync(client, "list_accounts");
        }

        var list = await _owner.GetFromJsonAsync<ApiTokenListResponse>("/api/tokens", Json, Ct);
        var listed = list!.Tokens.ShouldHaveSingleItem();
        listed.TokenHint.ShouldBe(token[..12]);
        listed.LastUsedAt.ShouldNotBeNull();
        listed.ExpiresAt.ShouldNotBeNull();

        (await _owner.DeleteAsync($"/api/tokens/{listed.Id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PostMcpAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var expiring = await CreateTokenAsync(_owner, ApiScope.Read);
        await DbAsync(db => db.ApiTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        (await PostMcpAsync(expiring)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_out_other_devices_revokes_all_tokens()
    {
        var token = await CreateTokenAsync(_owner, ApiScope.ReadWrite);
        (await _owner.PostAsync("/api/profile/sign-out-others", null, Ct)).EnsureSuccessStatusCode();
        (await PostMcpAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DbAsync(db => db.ApiTokens.CountAsync(Ct))).ShouldBe(0);
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:33418/callback", true)]
    [InlineData("http://127.0.0.1:5000/cb", true)]
    [InlineData("com.example.app:/oauth", true)]
    [InlineData("http://evil.example/callback", false)]
    [InlineData("https://claude.ai/cb#fragment", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    public void Redirect_addresses_must_be_https_loopback_or_an_app_scheme(string uri, bool allowed) =>
        OAuthServerEndpoints.IsAllowedRedirectUri(uri).ShouldBe(allowed);

    [Fact]
    public async Task An_app_connects_through_oauth_with_pkce_and_refreshes_its_tokens()
    {
        const string redirectUri = "https://claude.ai/api/mcp/auth_callback";
        var anonymous = _app.CreateClient(new() { AllowAutoRedirect = false });

        // Dynamic client registration.
        var registration = await anonymous.PostAsJsonAsync("/oauth2/register", new { client_name = "Claude", redirect_uris = new[] { redirectUri } }, Ct);
        registration.StatusCode.ShouldBe(HttpStatusCode.Created);
        var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("client_id").GetString()!;
        (await anonymous.PostAsJsonAsync("/oauth2/register", new { client_name = "Bad", redirect_uris = new[] { "http://evil.example/cb" } }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The authorization request goes on to the SPA's consent page with all parameters.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = $"response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&code_challenge={challenge}" +
            $"&code_challenge_method=S256&state=xyz&scope=mailnix.read%20mailnix.write&resource={Uri.EscapeDataString("http://localhost/mcp")}";
        var authorize = await anonymous.GetAsync($"/oauth2/authorize?{query}", Ct);
        authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        authorize.Headers.Location!.ToString().ShouldBe($"/connect?{query}");

        // The consent page shows the app, then the signed-in user agrees.
        var app = await _owner.GetFromJsonAsync<ConnectAppResponse>(
            $"/api/connect/app?clientId={clientId}&redirectUri={Uri.EscapeDataString(redirectUri)}&scope=mailnix.read%20mailnix.write", Json, Ct);
        app.ShouldBe(new ConnectAppResponse("Claude", "claude.ai", ApiScope.ReadWrite));
        (await _owner.GetAsync($"/api/connect/app?clientId={clientId}&redirectUri={Uri.EscapeDataString("https://claude.ai/other")}", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var consent = new ConnectRequest(clientId, redirectUri, "code", challenge, "S256", "xyz", "http://localhost/mcp", Approve: true, AllowChanges: false);
        var answer = await (await _owner.PostAsJsonAsync("/api/connect", consent, Json, Ct)).Content.ReadFromJsonAsync<ConnectResponse>(Json, Ct);
        var callback = new Uri(answer!.RedirectUrl);
        callback.GetLeftPart(UriPartial.Path).ShouldBe(redirectUri);
        var parameters = QueryHelpers.ParseQuery(callback.Query);
        parameters["state"].ToString().ShouldBe("xyz");
        parameters["iss"].ToString().ShouldBe("http://localhost");
        var code = parameters["code"].ToString();

        // A wrong verifier spends the code; the code works only once.
        (await TokenAsync(anonymous, ("grant_type", "authorization_code"), ("client_id", clientId), ("code", code), ("redirect_uri", redirectUri), ("code_verifier", new string('a', 43))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TokenAsync(anonymous, ("grant_type", "authorization_code"), ("client_id", clientId), ("code", code), ("redirect_uri", redirectUri), ("code_verifier", verifier)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        answer = await (await _owner.PostAsJsonAsync("/api/connect", consent, Json, Ct)).Content.ReadFromJsonAsync<ConnectResponse>(Json, Ct);
        code = QueryHelpers.ParseQuery(new Uri(answer!.RedirectUrl).Query)["code"].ToString();
        var tokens = await TokenAsync(anonymous, ("grant_type", "authorization_code"), ("client_id", clientId), ("code", code), ("redirect_uri", redirectUri), ("code_verifier", verifier));
        tokens.StatusCode.ShouldBe(HttpStatusCode.OK);
        var issued = await tokens.Content.ReadFromJsonAsync<JsonElement>(Ct);
        issued.GetProperty("token_type").GetString().ShouldBe("Bearer");
        issued.GetProperty("scope").GetString().ShouldBe("mailnix.read", "the user did not allow changes");
        var access = issued.GetProperty("access_token").GetString()!;
        var refresh = issued.GetProperty("refresh_token").GetString()!;

        await using (var client = await ConnectAsync(access))
        {
            (await CallAsync(client, "list_accounts")).GetArrayLength().ShouldBe(1);
            (await client.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).ShouldNotContain("create_rule");
        }

        var connection = (await _owner.GetFromJsonAsync<ApiTokenListResponse>("/api/tokens", Json, Ct))!.Tokens.ShouldHaveSingleItem();
        connection.Kind.ShouldBe(ApiTokenKind.Connection);
        connection.Name.ShouldBe("Claude");

        // Refreshing replaces both tokens; the old ones stop working, and no wider scope can be had.
        (await TokenAsync(anonymous, ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", refresh), ("scope", "mailnix.write")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var refreshed = await (await TokenAsync(anonymous, ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", refresh)))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        var newAccess = refreshed.GetProperty("access_token").GetString()!;
        newAccess.ShouldNotBe(access);
        (await PostMcpAsync(access)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TokenAsync(anonymous, ("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", refresh)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Revoking ends the connection.
        (await anonymous.PostAsync("/oauth2/revoke", new FormUrlEncodedContent([new("token", refreshed.GetProperty("refresh_token").GetString()!)]), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostMcpAsync(newAccess)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Declining_or_a_foreign_resource_sends_an_error_back_to_the_app()
    {
        const string redirectUri = "http://127.0.0.1:43123/callback?session=1";
        var anonymous = _app.CreateClient();
        var registration = await anonymous.PostAsJsonAsync("/oauth2/register", new { redirect_uris = new[] { redirectUri } }, Ct);
        var registered = await registration.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var clientId = registered.GetProperty("client_id").GetString()!;
        registered.GetProperty("client_name").GetString().ShouldBe("127.0.0.1");

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(new string('v', 50))));
        async Task<Dictionary<string, string>> DecideAsync(ConnectRequest request)
        {
            var answer = await (await _owner.PostAsJsonAsync("/api/connect", request, Json, Ct)).Content.ReadFromJsonAsync<ConnectResponse>(Json, Ct);
            answer!.RedirectUrl.ShouldStartWith(redirectUri + "&");
            return QueryHelpers.ParseQuery(new Uri(answer.RedirectUrl).Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        }

        var declined = await DecideAsync(new ConnectRequest(clientId, redirectUri, "code", challenge, "S256", "s1", null, Approve: false, AllowChanges: true));
        declined["error"].ShouldBe("access_denied");
        declined["state"].ShouldBe("s1");

        var foreign = await DecideAsync(new ConnectRequest(clientId, redirectUri, "code", challenge, "S256", "s2", "https://other.example/mcp", Approve: true, AllowChanges: true));
        foreign["error"].ShouldBe("invalid_target");

        var noPkce = await DecideAsync(new ConnectRequest(clientId, redirectUri, "code", null, null, "s3", null, Approve: true, AllowChanges: true));
        noPkce["error"].ShouldBe("invalid_request");
        (await DbAsync(db => db.AuthorizationCodes.CountAsync(Ct))).ShouldBe(0);
    }

    private Task<HttpResponseMessage> PostMcpAsync(string token)
    {
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" }, Ct);
    }

    private static Task<HttpResponseMessage> TokenAsync(HttpClient client, params (string Key, string Value)[] form) =>
        client.PostAsync("/oauth2/token", new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))), Ct);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<T> DbAsync<T>(Func<MailnixDbContext, Task<T>> query)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<MailnixDbContext>());
    }
}
