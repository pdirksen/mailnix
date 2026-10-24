using Mailnix.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailnix.Worker;

/// <summary>Runs one <see cref="MailboxWatcher"/> per enabled account and restarts it when the account changes.</summary>
internal sealed partial class MailboxSupervisor(
    IServiceScopeFactory scopes,
    IServiceProvider services,
    IWorkerSignals signals,
    IOptions<WorkerOptions> options,
    ILogger<MailboxSupervisor> logger) : BackgroundService
{
    private sealed record Running(Task Task, CancellationTokenSource Cancellation, DateTimeOffset UpdatedAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var watchers = new Dictionary<Guid, Running>();
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
        }
    }

    private async Task ReconcileAsync(Dictionary<Guid, Running> watchers, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accounts = await scope.ServiceProvider.GetRequiredService<MailnixDbContext>().MailAccounts.AsNoTracking()
            .Where(a => a.Enabled)
            .Select(a => new { a.Id, a.UpdatedAt })
            .ToDictionaryAsync(a => a.Id, a => a.UpdatedAt, ct);

        // Removed, paused or changed accounts (new credentials, other folders) and watchers that gave up.
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
}

public static class WorkerServiceCollectionExtensions
{
    /// <param name="runWatchers">Start the mailbox watchers in this process (AllInOne and Worker modes).</param>
    public static IServiceCollection AddMailnixWorker(this IServiceCollection services, IConfiguration configuration, bool runWatchers)
    {
        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection(WorkerOptions.Section));
        services.AddSingleton<IWorkerSignals, WorkerSignals>();
        services.TryAddSingleton<ILiveNotifier, NullLiveNotifier>();
        services.AddScoped<RunProcessor>();
        services.AddScoped<UndoProcessor>();
        if (runWatchers)
        {
            services.AddHostedService<MailboxSupervisor>();
        }

        return services;
    }
}
