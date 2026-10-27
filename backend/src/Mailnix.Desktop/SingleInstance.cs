using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Mailnix.Desktop;

/// <summary>One desktop app per data folder. Starting it again asks the running one to open mailnix in the browser.</summary>
[SupportedOSPlatform("windows")]
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _openRequest;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle openRequest)
    {
        _mutex = mutex;
        _openRequest = openRequest;
    }

    /// <returns>Null if another instance runs for this data folder; it was asked to open the browser unless <paramref name="quiet"/>.</returns>
    public static SingleInstance? TryAcquire(string dataDirectory, bool quiet)
    {
        var name = Name(dataDirectory);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var createdNew);
        var openRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}-open");
        if (createdNew)
        {
            return new SingleInstance(mutex, openRequest);
        }

        if (!quiet)
        {
            openRequest.Set();
        }

        openRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Calls <paramref name="open"/> (on a pool thread) whenever another start asks for it.</summary>
    public void OnOpenRequested(Action open) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_openRequest, (_, _) => open(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _openRequest.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static string Name(string dataDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant()));
        return $"mailnix-desktop-{Convert.ToHexString(hash)[..16]}";
    }
}
