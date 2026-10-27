namespace Mailnix.Api.Hosting;

/// <summary>Stops mailnix when its standard input closes (<see cref="MailnixHostOptions.StopWhenInputCloses"/>).</summary>
internal sealed partial class InputClosedShutdown(TextReader input, IHostApplicationLifetime lifetime, ILogger<InputClosedShutdown> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Console input has no cancellable read, so a background thread blocks on it.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Thread(() =>
        {
            try
            {
                while (input.ReadLine() is not null)
                {
                }
            }
            catch (IOException)
            {
                // A broken pipe means the same: the other side is gone.
            }

            closed.TrySetResult();
        })
        {
            IsBackground = true,
            Name = "mailnix input",
        };
        reader.Start();

        try
        {
            await closed.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        LogInputClosed();
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Standard input closed, stopping")]
    private partial void LogInputClosed();
}
