using Mailnix.Infrastructure.Data;
using Mailnix.Infrastructure.Options;
using Microsoft.AspNetCore.DataProtection;
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

            // ...and are themselves encrypted where the platform allows it (see KeyProtectionUpgrade for existing keys).
            var keyProtection = (configuration.GetSection(KeyProtectionOptions.Section).Get<KeyProtectionOptions>() ?? new KeyProtectionOptions()).Resolve();
            if (OperatingSystem.IsWindows() && keyProtection is KeyProtectionMode.WindowsUser or KeyProtectionMode.WindowsMachine)
            {
                dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: keyProtection == KeyProtectionMode.WindowsMachine);
            }
        }

        services.AddScoped<DatabaseInitializer>();
        services.AddScoped<KeyProtectionUpgrade>();
        services.AddSingleton<MailSecretProtector>();
        services.AddScoped<RuleSetLoader>();
        services.AddSingleton(TimeProvider.System);

        return services;
    }
}
