using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Mailnix.Infrastructure.Options;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public class DatabaseOptions
{
    public const string Section = "Mailnix:Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Empty for SQLite: mailnix.db in the <see cref="DataDirectory"/>.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Apply pending migrations on startup. Convenient for self-hosting; SaaS deployments may migrate in a release step instead.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>Fills in the default SQLite database, which depends on the data directory.</summary>
    public void ApplyDefaults(IConfiguration configuration)
    {
        if (Provider == DatabaseProvider.Sqlite && string.IsNullOrWhiteSpace(ConnectionString))
        {
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory.Resolve(configuration), "mailnix.db") }.ToString();
        }
    }
}

/// <summary>Optional admin account created on first start (self-hosted), e.g. via MAILNIX__BOOTSTRAP__ADMINEMAIL.</summary>
public class BootstrapOptions
{
    public const string Section = "Mailnix:Bootstrap";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}
