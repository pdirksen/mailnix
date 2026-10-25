using Mailnix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

/// <summary>
/// Runs one <see cref="MailboxWatcher"/> per account this worker holds the lease for (see <see cref="LeaseManager"/>)
/// and restarts it when the account changes. Several worker processes can run side by side.
/// </summary>
internal sealed partial class MailboxSupervisor(
    IServiceScopeFactory scopes,
    IServiceProvider services,
    IWorkerSignals signals,
    IOptions<WorkerOptions> options,
    ILogger<MailboxSupervisor> logger) : BackgroundService
{
    private sealed record Running(Task Task, CancellationTokenSource Cancellation, DateTimeOffset UpdatedAt);

    /// <summary>Accounts with a running watcher, for the queue poll (which runs alongside the reconcile loop).</summary>
    private volatile Guid[] _held = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var watchers = new Dictionary<Guid, Running>();
        var queuePoll = PollQueueAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(watchers, stoppingToken);
                }
                catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
                {
                    LogReconcileFailed(ex);
                }

                await signals.WaitForAccountChangesAsync(options.Value.ReconcileInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync(watchers.Values);
            await ReleaseLeasesAsync();
            await queuePoll;
        }
    }

    /// <summary>
    /// Runs queued by an API process (run now, undo, confirmations) wake the account's watcher. In-process signals
    /// cannot reach a separate worker process, so the queue is checked every few seconds.
    /// </summary>
    private async Task PollQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Value.QueuePollInterval, ct);
                var held = _held;
                if (held.Length == 0)
                {
                    continue;
                }

                await using var scope = scopes.CreateAsyncScope();
                var queued = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().Runs.AsNoTracking()
                    .Where(r => r.State == RunState.Queued && held.Contains(r.AccountId))
                    .Select(r => r.AccountId)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var accountId in queued)
                {
                    signals.Wake(accountId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                LogQueuePollFailed(ex);
            }
        }
    }

    private async Task ReleaseLeasesAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<LeaseManager>().ReleaseAllAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // The leases expire on their own.
            LogReleaseFailed(ex);
        }
    }

    private async Task ReconcileAsync(Dictionary<Guid, Running> watchers, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<LeaseManager>().ReconcileAsync(ct);
        _held = [.. accounts.Keys];

        // Lost leases, removed, paused or changed accounts (new credentials, other folders) and watchers that gave up.
        foreach (var (id, running) in watchers.ToList())
        {
            if (!accounts.TryGetValue(id, out var updatedAt) || updatedAt != running.UpdatedAt || running.Task.IsCompleted)
            {
                await StopAsync([running]);
                watchers.Remove(id);
            }
        }

        foreach (var (id, updatedAt) in accounts.Where(a => !watchers.ContainsKey(a.Key)))
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watcher = ActivatorUtilities.CreateInstance<MailboxWatcher>(services, id);
            watchers[id] = new Running(Task.Run(() => watcher.RunAsync(cancellation.Token), CancellationToken.None), cancellation, updatedAt);
        }
    }

    private static async Task StopAsync(IEnumerable<Running> running)
    {
        var list = running.ToList();
        foreach (var watcher in list)
        {
            await watcher.Cancellation.CancelAsync();
        }

        await Task.WhenAll(list.Select(w => w.Task)).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
        foreach (var watcher in list)
        {
            watcher.Cancellation.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not reconcile mailbox watchers")]
    private partial void LogReconcileFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the run queue")]
    private partial void LogQueuePollFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not give the mailbox leases back; they expire on their own")]
    private partial void LogReleaseFailed(Exception exception);
}

public static class WorkerServiceCollectionExtensions
{
    /// <param name="runWatchers">Start the mailbox watchers in this process (AllInOne and Worker modes).</param>
    public static IServiceCollection AddMailnixWorker(this IServiceCollection services, IConfiguration configuration, bool runWatchers)
    {
        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection(WorkerOptions.Section));
        services.AddSingleton<IWorkerSignals, WorkerSignals>();
        services.AddSingleton<WorkerIdentity>();
        services.AddScoped<LeaseManager>();
        services.TryAddSingleton<ILiveNotifier, NullLiveNotifier>();
        services.AddScoped<RunProcessor>();
        services.AddScoped<UndoProcessor>();
        services.AddScoped<TrashPurger>();
        if (runWatchers)
        {
            services.AddHostedService<MailboxSupervisor>();
            services.AddHostedService<RuleScheduler>();
        }

        return services;
    }
}
