using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.OAuth;
using Mailnix.Infrastructure.Options;
using Mailnix.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mailnix.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <param name="persistDataProtectionKeys">
    /// False only for tooling that boots the app without a database (build-time OpenAPI generation).
    /// </param>
    public static IServiceCollection AddMailnixInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool persistDataProtectionKeys = true)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section));
        services.AddOptions<BootstrapOptions>().Bind(configuration.GetSection(BootstrapOptions.Section));
        services.AddOptions<KeyProtectionOptions>().Bind(configuration.GetSection(KeyProtectionOptions.Section));

        var database = configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        switch (database.Provider)
        {
            case DatabaseProvider.Sqlite:
                services.AddDbContext<MailnixDbContext, SqliteMailnixDbContext>(o => o.UseSqlite(database.ConnectionString));
                break;
            case DatabaseProvider.Postgres:
                services.AddDbContext<MailnixDbContext, PostgresMailnixDbContext>(o => o.UseNpgsql(database.ConnectionString));
                break;
            default:
                throw new InvalidOperationException($"Unsupported database provider '{database.Provider}'.");
        }

        // Keys live in the database so every API/worker replica can decrypt stored mailbox secrets.
        var dataProtection = services.AddDataProtection().SetApplicationName("mailnix");
        if (persistDataProtectionKeys)
        {
            dataProtection.PersistKeysToDbContext<MailnixDbContext>();

            // ...and are themselves encrypted: with a master key kept outside the database, or with Windows DPAPI
            // (see KeyProtectionUpgrade for keys stored before).
            var keyOptions = configuration.GetSection(KeyProtectionOptions.Section).Get<KeyProtectionOptions>() ?? new KeyProtectionOptions();
            var keyProtection = keyOptions.Resolve();

            // Also available when not used for new keys, so keys written with it can still be read.
            if (keyOptions.LoadMasterKey() is { } masterKey)
            {
                services.AddSingleton(masterKey);
                if (keyProtection == KeyProtectionMode.MasterKey)
                {
                    services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new MasterKeyXmlEncryptor(masterKey));
                }
            }

            if (OperatingSystem.IsWindows() && keyProtection is KeyProtectionMode.WindowsUser or KeyProtectionMode.WindowsMachine)
            {
                dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: keyProtection == KeyProtectionMode.WindowsMachine);
            }
        }

        // OAuth for Google and Microsoft mailboxes; one long-lived HttpClient (pooled connections, DNS refresh).
        services.AddOptions<OAuthOptions>().Bind(configuration.GetSection(OAuthOptions.Section));
        services.AddMemoryCache();
        services.AddSingleton(sp => new OAuthClient(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OAuthOptions>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<OAuthStateProtector>();
        services.AddSingleton<AccountCredentials>();

        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<KeyProtectionUpgrade>();
        services.AddSingleton<MailSecretProtector>();
        services.AddScoped<RuleSetLoader>();
        services.AddScoped<StatsRecorder>();
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
