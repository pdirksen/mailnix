using Mailnix.Infrastructure.Identity;
using Mailnix.Infrastructure.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Infrastructure.Data;

/// <summary>Applies migrations and seeds the data every installation needs (default tenant, roles, optional bootstrap admin).</summary>
public sealed partial class DatabaseInitializer(
    MailnixDbContext db,
    RoleManager<AppRole> roleManager,
    UserManager<AppUser> userManager,
    IOptions<DatabaseOptions> databaseOptions,
    IOptions<BootstrapOptions> bootstrapOptions,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (databaseOptions.Value.AutoMigrate)
        {
            EnsureSqliteDirectoryExists();
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (!await db.Tenants.AnyAsync(t => t.Id == Tenant.DefaultId, cancellationToken))
        {
            db.Tenants.Add(new Tenant { Id = Tenant.DefaultId, Name = "Default", CreatedAt = timeProvider.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                ThrowOnFailure(await roleManager.CreateAsync(new AppRole(role)), $"create role {role}");
            }
        }

        await SeedBootstrapAdminAsync();
    }

    private async Task SeedBootstrapAdminAsync()
    {
        var (email, password) = (bootstrapOptions.Value.AdminEmail, bootstrapOptions.Value.AdminPassword);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var admin = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            TenantId = Tenant.DefaultId,
            CreatedAt = timeProvider.GetUtcNow(),
        };
        ThrowOnFailure(await userManager.CreateAsync(admin, password), "create bootstrap admin");
        ThrowOnFailure(await userManager.AddToRolesAsync(admin, [Roles.Admin, Roles.User]), "assign bootstrap admin roles");
        LogBootstrapAdminCreated(email);
    }

    private void EnsureSqliteDirectoryExists()
    {
        if (databaseOptions.Value.Provider != DatabaseProvider.Sqlite)
        {
            return;
        }

        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(databaseOptions.Value.ConnectionString).DataSource;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created bootstrap admin {Email}")]
    private partial void LogBootstrapAdminCreated(string email);

    private static void ThrowOnFailure(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to {operation}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
