using Mailnix.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Mailnix.Integration.Tests;

/// <summary>Upgrades of databases that already hold data (the app tests always start from an empty one).</summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mailnix-migration-{Guid.NewGuid():N}.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    private SqliteMailnixDbContext Open() =>
        new(new DbContextOptionsBuilder<SqliteMailnixDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options);

    [Fact]
    public async Task Users_from_before_the_settings_keep_working_and_lose_the_never_chosen_language()
    {
        await using (var db = Open())
        {
            // The schema of milestone 9, with a user as it was created then (language "en" by default).
            await db.GetService<IMigrator>().MigrateAsync("20261025081833_Saas", Ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Tenants" ("Id", "Name", "CreatedAt") VALUES ('00000000-0000-0000-0000-000000000001', 'Default', '2026-10-01 00:00:00+00:00');
                INSERT INTO "AspNetUsers" ("Id", "TenantId", "TimeZone", "Language", "CreatedAt", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "UserName", "Email")
                VALUES ('6a4b0f0e-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'Europe/Berlin', 'en', '2026-10-01 00:00:00+00:00', 0, 0, 0, 1, 0, 'old@example.com', 'old@example.com');
                """,
                Ct);
        }

        await using (var db = Open())
        {
            await db.Database.MigrateAsync(Ct);
            var user = await db.Users.SingleAsync(Ct);
            user.Language.ShouldBeNull();
            user.Addresses.ShouldBeEmpty();
            user.TimeZone.ShouldBe("Europe/Berlin");
        }
    }
}
