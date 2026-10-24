using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mailnix.Infrastructure.Data;

// Used only by `dotnet ef migrations add`; connection strings are placeholders and never opened.

internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteMailnixDbContext>
{
    public SqliteMailnixDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteMailnixDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

internal sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresMailnixDbContext>
{
    public PostgresMailnixDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresMailnixDbContext>().UseNpgsql("Host=localhost;Database=mailnix_design").Options);
}
