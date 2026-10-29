using System.Data.Common;
using Mailnix.Infrastructure.Data;
using Mailnix.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailnix.Integration.Tests;

/// <summary>How worker processes share the mailboxes through leases (several "workers" on one database).</summary>
public sealed class LeaseTests : IAsyncLifetime
{
    private readonly MailnixAppFactory _app = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly List<Guid> _accounts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        (await _app.CreateApiClient().RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        for (var i = 0; i < 4; i++)
        {
            _accounts.Add(await Seed.AccountAsync(_app, "owner@example.com", $"Account {i}"));
        }
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>One worker process, as far as leases go.</summary>
    private sealed class Worker(LeaseTests test)
    {
        public WorkerIdentity Identity { get; } = new();

        public async Task<HashSet<Guid>> ReconcileAsync(IInterceptor? interceptor = null)
        {
            await using var scope = test._app.Services.CreateAsyncScope();
            var options = new DbContextOptionsBuilder<SqliteMailnixDbContext>(scope.ServiceProvider.GetRequiredService<DbContextOptions<SqliteMailnixDbContext>>());
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }

            await using var db = new SqliteMailnixDbContext(options.Options);
            var leases = new LeaseManager(db, Identity, Options.Create(new WorkerOptions()), test._time);
            return [.. (await leases.ReconcileAsync(Ct)).Keys];
        }

        public async Task ReleaseAsync()
        {
            await using var scope = test._app.Services.CreateAsyncScope();
            await new LeaseManager(scope.ServiceProvider.GetRequiredService<MailnixDbContext>(), Identity, Options.Create(new WorkerOptions()), test._time).ReleaseAllAsync(Ct);
        }
    }

    [Fact]
    public async Task Workers_share_the_mailboxes_and_never_hold_the_same_one()
    {
        Worker a = new(this), b = new(this);

        (await a.ReconcileAsync()).Count.ShouldBe(4, "alone, a worker takes everything");

        // A second worker appears: the first hands mailboxes back one at a time, the second picks them up.
        HashSet<Guid> heldByA = [], heldByB = [];
        for (var pass = 0; pass < 4; pass++)
        {
            _time.Now += TimeSpan.FromSeconds(30);
            heldByB = await b.ReconcileAsync();
            heldByA = await a.ReconcileAsync();
        }

        heldByA.Count.ShouldBe(2);
        heldByB.Count.ShouldBe(2);
        heldByA.Overlaps(heldByB).ShouldBeFalse();
        heldByA.Union(heldByB).ShouldBe(_accounts, ignoreOrder: true);
    }

    [Fact]
    public async Task Mailboxes_of_a_dead_worker_are_taken_over_after_the_lease_expires()
    {
        Worker a = new(this), b = new(this);
        await a.ReconcileAsync();

        // Still leased: nothing to take.
        _time.Now += TimeSpan.FromSeconds(30);
        (await b.ReconcileAsync()).ShouldBeEmpty();

        // A stops renewing (crashed); after the lease duration B takes everything over.
        _time.Now += TimeSpan.FromMinutes(3);
        (await b.ReconcileAsync()).Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_worker_shutting_down_hands_its_mailboxes_over_at_once()
    {
        Worker a = new(this), b = new(this);
        await a.ReconcileAsync();
        await a.ReleaseAsync();

        _time.Now += TimeSpan.FromSeconds(1);
        (await b.ReconcileAsync()).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Paused_accounts_are_released()
    {
        Worker a = new(this);
        await a.ReconcileAsync();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.Where(x => x.Id == _accounts[0]).ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, false), Ct);
        }

        var held = await a.ReconcileAsync();

        held.ShouldNotContain(_accounts[0]);
        held.Count.ShouldBe(3);
    }

    [Fact]
    public async Task An_account_paused_during_a_pass_does_not_cost_another_mailbox_its_watcher()
    {
        Worker a = new(this);
        await a.ReconcileAsync();
        var pause = new PauseBeforeCountingAccounts(this, _accounts[1]);

        var held = await a.ReconcileAsync(pause);

        pause.Paused.ShouldBeTrue();
        held.ShouldBe(_accounts.Where(id => id != _accounts[1]), ignoreOrder: true);
    }

    /// <summary>Pauses an account right before the pass counts the enabled accounts (as another request might).</summary>
    private sealed class PauseBeforeCountingAccounts(LeaseTests test, Guid accountId) : DbCommandInterceptor
    {
        public bool Paused { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Paused && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal) && command.CommandText.Contains("FROM \"MailAccounts\"", StringComparison.Ordinal))
            {
                Paused = true;
                await using var scope = test._app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts
                    .Where(x => x.Id == accountId)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Enabled, false), cancellationToken);
            }

            return result;
        }
    }
}
