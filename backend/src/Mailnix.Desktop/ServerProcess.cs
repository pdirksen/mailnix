using System.Diagnostics;
using System.Text;

namespace Mailnix.Desktop;

/// <param name="FileName">mailnix.exe (tests: the API's apphost from their build).</param>
/// <param name="Port">Listens on http://localhost:&lt;port&gt; only.</param>
internal sealed record ServerStartInfo(string FileName, string DataDirectory, int Port, string LogFile);

/// <summary>
/// The mailnix server as a hidden child process: the desktop environment and data folder, output to a log file, and a
/// clean stop by closing its standard input (Mailnix:StopWhenInputCloses).
/// </summary>
internal sealed class ServerProcess(ServerStartInfo start) : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock _logLock = new();
    private Process? _process;
    private StreamWriter? _log;
    private volatile bool _stopping;

    public Uri Url { get; } = new($"http://localhost:{start.Port}/");

    internal int? ProcessId => _process?.Id;

    /// <summary>The process ended without <see cref="StopAsync"/>; the argument is its exit code. Raised on a pool thread.</summary>
    public event Action<int>? ExitedUnexpectedly;

    /// <summary>Starts the process and waits until it answers its health check.</summary>
    /// <exception cref="ServerStartException">It ended or didn't get ready in time.</exception>
    public async Task StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(start.LogFile)!);
        // The previous run's log stays around for one more start, e.g. to see why it stopped.
        if (File.Exists(start.LogFile))
        {
            File.Move(start.LogFile, Path.ChangeExtension(start.LogFile, ".previous.log"), overwrite: true);
        }

        _log = new StreamWriter(new FileStream(start.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        var info = new ProcessStartInfo(start.FileName)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(start.FileName))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("--urls");
        info.ArgumentList.Add($"http://localhost:{start.Port}");
        // ASPNETCORE_ wins over DOTNET_, and may be set on a developer's machine.
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Desktop";
        info.Environment["Mailnix__DataDirectory"] = start.DataDirectory;
        info.Environment["Mailnix__StopWhenInputCloses"] = "true";

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => WriteLog(e.Data);
        process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        process.Exited += (_, _) =>
        {
            if (!_stopping)
            {
                ExitedUnexpectedly?.Invoke(process.ExitCode);
            }
        };
        WriteLog($"Starting {start.FileName} on {Url} with data in {start.DataDirectory}");
        process.Start();
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitUntilReadyAsync(process, ct);
    }

    /// <summary>Closes standard input and waits; kills the process if it hasn't ended within <paramref name="grace"/>.</summary>
    /// <returns>False if it had to be killed.</returns>
    public async Task<bool> StopAsync(TimeSpan grace)
    {
        _stopping = true;
        if (_process is not { } process || process.HasExited)
        {
            return true;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Already gone.
        }

        using var timeout = new CancellationTokenSource(grace);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            WriteLog($"mailnix did not stop within {grace.TotalSeconds:0} seconds and was ended");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return false;
        }
    }

    public void Dispose()
    {
        _process?.Dispose();
        lock (_logLock)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    private async Task WaitUntilReadyAsync(Process process, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var health = new Uri(Url, "healthz");
        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new ServerStartException($"mailnix ended during startup (exit code {process.ExitCode}).");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new ServerStartException($"mailnix did not answer at {health} within {StartTimeout.TotalSeconds:0} seconds.");
            }

            try
            {
                using var response = await http.GetAsync(health, ct);
                if (response.IsSuccessStatusCode)
                {
                    WriteLog($"Ready at {Url}");
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // The request timed out.
            }

            await Task.Delay(250, ct);
        }
    }

    private void WriteLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logLock)
        {
            _log?.WriteLine(line);
        }
    }
}

internal sealed class ServerStartException(string message) : Exception(message);
