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

    public string ConnectionString { get; set; } = "Data Source=data/mailnix.db";

    /// <summary>Apply pending migrations on startup. Convenient for self-hosting; SaaS deployments may migrate in a release step instead.</summary>
    public bool AutoMigrate { get; set; } = true;
}

/// <summary>Optional admin account created on first start (self-hosted), e.g. via MAILNIX__BOOTSTRAP__ADMINEMAIL.</summary>
public class BootstrapOptions
{
    public const string Section = "Mailnix:Bootstrap";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }
}
