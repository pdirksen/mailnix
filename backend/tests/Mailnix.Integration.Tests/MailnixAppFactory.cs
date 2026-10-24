using System.Net.Http.Json;
using Mailnix.Api.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Integration.Tests;

/// <summary>Boots the real API (AllInOne mode) against a throwaway SQLite database file.</summary>
public sealed class MailnixAppFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"mailnix-test-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> _settings = [];
    private readonly List<Action<IServiceCollection>> _services = [];

    public MailnixAppFactory WithSetting(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>Replaces or adds services, e.g. fakes for the mail server.</summary>
    public MailnixAppFactory WithServices(Action<IServiceCollection> configure)
    {
        _services.Add(configure);
        return this;
    }

    /// <summary>A client that sends the anti-CSRF header like the SPA does, and keeps cookies.</summary>
    public HttpClient CreateApiClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        client.DefaultRequestHeaders.Add(CsrfHeaderMiddleware.HeaderName, CsrfHeaderMiddleware.HeaderValue);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Mailnix:Database:Provider", "Sqlite");
        builder.UseSetting("Mailnix:Database:ConnectionString", $"Data Source={_databasePath};Pooling=False");

        // Mailbox watchers only run in tests that ask for them (WithSetting("Mailnix:Worker:Enabled", "true")).
        builder.UseSetting("Mailnix:Worker:Enabled", "false");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => _services.ForEach(configure => configure(services)));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_databasePath);
    }
}

internal static class HttpClientAuthExtensions
{
    public const string DefaultPassword = "correct horse battery";

    public static Task<HttpResponseMessage> RegisterAsync(this HttpClient client, string email, string password = DefaultPassword) =>
        client.PostAsJsonAsync("/api/auth/register", new { email, password, displayName = "Test User" });

    public static Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email, string password = DefaultPassword) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password, rememberMe = false });
}
