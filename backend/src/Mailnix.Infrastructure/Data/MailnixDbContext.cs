using Mailnix.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Mailnix.Infrastructure.Data;

/// <summary>
/// Provider-neutral model. Each database provider has its own subclass (and therefore its own migrations set),
/// because SQLite and PostgreSQL migrations are not interchangeable.
/// </summary>
public abstract class MailnixDbContext(DbContextOptions options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>
    /// The tenant of the signed-in user (set per request): accounts, rules, lists and runs of other tenants are
    /// filtered out, as a second line of defense behind the per-user checks. Null for the worker and system tasks.
    /// </summary>
    public Guid? TenantFilter { get; set; }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();

    public DbSet<MailRule> Rules => Set<MailRule>();

    public DbSet<RuleReference> RuleReferences => Set<RuleReference>();

    public DbSet<RuleListReference> RuleListReferences => Set<RuleListReference>();

    public DbSet<NamedListEntity> NamedLists => Set<NamedListEntity>();

    public DbSet<RuleRun> Runs => Set<RuleRun>();

    public DbSet<ActionLogEntry> ActionLog => Set<ActionLogEntry>();

    public DbSet<FolderState> FolderStates => Set<FolderState>();

    public DbSet<TrashObservation> TrashObservations => Set<TrashObservation>();

    public DbSet<StatsDaily> StatsDaily => Set<StatsDaily>();

    public DbSet<SenderDaily> SenderDaily => Set<SenderDaily>();

    public DbSet<SecurityDaily> SecurityDaily => Set<SecurityDaily>();

    public DbSet<FolderSnapshot> FolderSnapshots => Set<FolderSnapshot>();

    public DbSet<MailboxLease> MailboxLeases => Set<MailboxLease>();

    public DbSet<WorkerInstance> Workers => Set<WorkerInstance>();

    public DbSet<LiveEventRecord> LiveEvents => Set<LiveEventRecord>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Rows that hang off a tenant-filtered entity (stats, action log, leases, ...) are always read by account or
        // user id, so EF's warning about required navigations to filtered entities does not apply.
        optionsBuilder.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Tenant>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
        });

        builder.Entity<AppUser>(e =>
        {
            e.HasOne(u => u.Tenant).WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(u => u.TenantId);
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.TimeZone).HasMaxLength(100);
            e.Property(u => u.Language).HasMaxLength(10);
            e.PrimitiveCollection(u => u.Addresses);
        });

        builder.Entity<MailAccount>(e =>
        {
            e.HasQueryFilter(a => TenantFilter == null || a.TenantId == TenantFilter);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.TenantId, a.UserId });
            e.Property(a => a.Name).HasMaxLength(200);
            e.Property(a => a.Provider).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Host).HasMaxLength(253);
            e.Property(a => a.Security).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Username).HasMaxLength(320);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.LastErrorCode).HasConversion<string>().HasMaxLength(40);
            e.Property(a => a.LastError).HasMaxLength(1000);
            e.Property(a => a.FolderDelimiter).HasMaxLength(1);
            e.ComplexProperty(a => a.SpecialFolders);
        });

        builder.Entity<MailRule>(e =>
        {
            e.HasQueryFilter(r => TenantFilter == null || r.TenantId == TenantFilter);
            e.HasOne(r => r.Account).WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.TenantId, r.UserId });
            e.HasIndex(r => new { r.AccountId, r.Priority });
            e.Property(r => r.Name).HasMaxLength(200);
            e.Property(r => r.Description).HasMaxLength(2000);
            e.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Schedule).HasMaxLength(100);
            e.HasMany(r => r.References).WithOne().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.ListReferences).WithOne().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RuleReference>(e =>
        {
            e.HasKey(x => new { x.RuleId, x.ReferencedRuleId });
            e.HasIndex(x => x.ReferencedRuleId);

            // Referenced rules cannot be deleted while in use; the API reports which rules depend on them.
            e.HasOne<MailRule>().WithMany().HasForeignKey(x => x.ReferencedRuleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RuleListReference>(e =>
        {
            e.HasKey(x => new { x.RuleId, x.ListId });
            e.HasIndex(x => x.ListId);
            e.HasOne<NamedListEntity>().WithMany().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<NamedListEntity>(e =>
        {
            e.HasQueryFilter(l => TenantFilter == null || l.TenantId == TenantFilter);
            e.HasIndex(l => new { l.TenantId, l.UserId });
            e.Property(l => l.Name).HasMaxLength(200);
            e.Property(l => l.Description).HasMaxLength(2000);
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasMany(l => l.Entries).WithOne().HasForeignKey(x => x.ListId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RuleRun>(e =>
        {
            e.HasQueryFilter(r => TenantFilter == null || r.TenantId == TenantFilter);
            e.HasOne<MailAccount>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.UserId, r.CreatedAt });
            e.HasIndex(r => new { r.AccountId, r.State });
            e.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.State).HasConversion<string>().HasMaxLength(30);
            e.Property(r => r.Folder).HasMaxLength(500);
            e.Property(r => r.Error).HasMaxLength(2000);
            e.Property(r => r.SearchFilter).HasMaxLength(2000);
        });

        builder.Entity<ActionLogEntry>(e =>
        {
            e.HasOne<RuleRun>().WithMany().HasForeignKey(a => a.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => a.RunId);
            e.HasIndex(a => new { a.RuleId, a.CreatedAt });
            e.HasIndex(a => new { a.AccountId, a.TargetFolder, a.TargetUid });
            e.Property(a => a.Action).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.RuleName).HasMaxLength(200);
            e.Property(a => a.Keyword).HasMaxLength(100);
            e.Property(a => a.SourceFolder).HasMaxLength(500);
            e.Property(a => a.TargetFolder).HasMaxLength(500);
            e.Property(a => a.MessageId).HasMaxLength(500);
            e.Property(a => a.From).HasMaxLength(320);
            e.Property(a => a.Subject).HasMaxLength(300);
            e.Property(a => a.Error).HasMaxLength(1000);
        });

        builder.Entity<FolderState>(e =>
        {
            e.HasKey(f => new { f.AccountId, f.Folder });
            e.HasOne<MailAccount>().WithMany().HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.Property(f => f.Folder).HasMaxLength(500);
        });

        builder.Entity<StatsDaily>(e =>
        {
            e.HasKey(s => new { s.AccountId, s.Date, s.RuleId, s.Metric });
            e.HasOne<MailAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.Date });
            e.Property(s => s.Metric).HasMaxLength(30);
        });

        builder.Entity<SenderDaily>(e =>
        {
            e.HasKey(s => new { s.AccountId, s.Date, s.Domain });
            e.HasOne<MailAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.Date });
            e.Property(s => s.Domain).HasMaxLength(253);
        });

        builder.Entity<SecurityDaily>(e =>
        {
            e.HasKey(s => new { s.AccountId, s.Date });
            e.HasOne<MailAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.Date });
        });

        builder.Entity<FolderSnapshot>(e =>
        {
            e.HasKey(s => new { s.AccountId, s.Folder, s.Date });
            e.HasOne<MailAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.Date });
            e.Property(s => s.Folder).HasMaxLength(500);
        });

        builder.Entity<MailboxLease>(e =>
        {
            e.HasKey(l => l.AccountId);
            e.HasOne<MailAccount>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.WorkerId);
            e.Property(l => l.WorkerId).HasMaxLength(200);
        });

        builder.Entity<WorkerInstance>(e =>
        {
            e.Property(w => w.Id).HasMaxLength(200);
            e.Property(w => w.Host).HasMaxLength(200);
        });

        builder.Entity<LiveEventRecord>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(50);
            e.HasIndex(l => l.CreatedAt);
        });

        builder.Entity<Invitation>(e =>
        {
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasIndex(i => i.TenantId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.Property(i => i.Email).HasMaxLength(320);
            e.Property(i => i.TokenHash).HasMaxLength(100);
        });

        builder.Entity<TrashObservation>(e =>
        {
            e.HasKey(t => new { t.AccountId, t.Folder, t.Uid });
            e.HasOne<MailAccount>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.Property(t => t.Folder).HasMaxLength(500);
        });

        builder.Entity<NamedListEntryEntity>(e =>
        {
            // Ids are assigned in code; this lets EF recognize entries added to a loaded list as new.
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.ListId, x.Value }).IsUnique();
            e.Property(x => x.Value).HasMaxLength(320);
            e.Property(x => x.Note).HasMaxLength(500);
        });
    }
}

public class SqliteMailnixDbContext(DbContextOptions<SqliteMailnixDbContext> options) : MailnixDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare or order DateTimeOffset values; store them as sortable binary instead.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}

public class PostgresMailnixDbContext(DbContextOptions<PostgresMailnixDbContext> options) : MailnixDbContext(options);
