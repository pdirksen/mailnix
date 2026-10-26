using System.Net.Http.Json;
using Mailnix.Api.Auth;
using Mailnix.Api.Hosting;
using Mailnix.Infrastructure.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Mailnix.Integration.Tests;

/// <summary>The data directory holds the SQLite database and settings that outlive updates.</summary>
public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mailnix-data-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // The default connection string pools connections, which keep the database file open.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_database_and_settings_come_from_the_data_directory()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "appsettings.json"), """{ "Mailnix": { "Auth": { "AllowRegistration": false } } }""", Ct);

        await using var app = new MailnixAppFactory()
            .WithSetting(DataDirectory.Key, _directory)
            .WithSetting("Mailnix:Database:ConnectionString", "");
        var client = app.CreateApiClient();
        (await client.RegisterAsync("first@example.com")).EnsureSuccessStatusCode();

        File.Exists(Path.Combine(_directory, "mailnix.db")).ShouldBeTrue();
        var config = await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct);
        config!.RegistrationOpen.ShouldBeFalse("the settings in the data directory close registration after the first user");
    }

    [Fact]
    public void Settings_in_the_data_directory_rank_between_the_app_files_and_the_environment()
    {
        var appFolder = Path.Combine(_directory, "app");
        var dataFolder = Path.Combine(_directory, "data");
        Directory.CreateDirectory(appFolder);
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(Path.Combine(appFolder, "appsettings.json"), """{ "A": "app", "B": "app", "C": "app" }""");
        File.WriteAllText(Path.Combine(dataFolder, "appsettings.json"), """{ "A": "data", "B": "data" }""");

        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(appFolder, "appsettings.json"));
        // Stands in for environment variables and the command line.
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["B"] = "environment", [DataDirectory.Key] = dataFolder });
        configuration.AddDataDirectorySettings();

        (configuration["A"], configuration["B"], configuration["C"]).ShouldBe(("data", "environment", "app"));
    }
}
