using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Mailnix.Api.Auth;
using Mailnix.Api.Hosting;
using Mailnix.Desktop;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailnix.Integration.Tests;

/// <summary>The Windows desktop app's own logic, and the server it runs as a child process.</summary>
public sealed class DesktopTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mailnix-desktop-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void An_installation_keeps_its_data_outside_the_app_folder()
    {
        var root = Path.Combine(_directory, "mailnix");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        var localAppData = Path.Combine(_directory, "LocalAppData");

        var unpacked = DesktopPaths.For(current, localAppData);
        unpacked.Installed.ShouldBeFalse("there is no Update.exe next to the folder");
        unpacked.DataDirectory.ShouldBe(Path.Combine(current, "data"));

        File.WriteAllText(Path.Combine(root, "Update.exe"), "");
        var installed = DesktopPaths.For(current + Path.DirectorySeparatorChar, localAppData);
        installed.Installed.ShouldBeTrue();
        installed.DataDirectory.ShouldBe(Path.Combine(localAppData, "mailnix-data"));
        installed.ServerExecutable.ShouldBe(Path.Combine(current, "mailnix.exe"));
    }

    [Theory]
    [InlineData("""{ "Mailnix": { "Desktop": { "Port": 5999 } } }""", 5999)]
    [InlineData("""{ "mailnix": { "desktop": { "port": 5999, }, }, // comment""" + "\n}", 5999)]
    [InlineData("""{ "Mailnix": { "Auth": { "AllowRegistration": true } } }""", DesktopSettings.DefaultPort)]
    [InlineData("""{ "Mailnix": { "Desktop": { "Port": 70000 } } }""", DesktopSettings.DefaultPort)]
    [InlineData("""{ "Mailnix": { "Desktop": { "Port": "5999" } } }""", DesktopSettings.DefaultPort)]
    [InlineData("not json", DesktopSettings.DefaultPort)]
    public void The_port_comes_from_the_settings_in_the_data_folder(string json, int port)
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(file, json);

        DesktopSettings.Read(file).Port.ShouldBe(port);
    }

    [Fact]
    public void Without_a_settings_file_the_default_port_applies() =>
        DesktopSettings.Read(Path.Combine(_directory, "missing.json")).Port.ShouldBe(DesktopSettings.DefaultPort);

    [Fact]
    public void A_port_in_use_is_not_free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Ports.IsFree(((IPEndPoint)listener.LocalEndpoint).Port).ShouldBeFalse();
        }
        finally
        {
            listener.Stop();
        }

        Ports.IsFree(Ports.FindFree()).ShouldBeTrue();
    }

    [Fact]
    public async Task Closed_input_stops_the_server()
    {
        var lifetime = new FakeLifetime();
        using var service = new InputClosedShutdown(new StringReader("ignored\n"), lifetime, NullLogger<InputClosedShutdown>.Instance);

        await service.StartAsync(Ct);

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task The_server_runs_with_the_desktop_settings_and_stops_when_asked()
    {
        using var server = NewServer();
        var stopwatch = Stopwatch.StartNew();
        await server.StartAsync(Ct);

        using var client = new HttpClient { BaseAddress = server.Url };
        client.DefaultRequestHeaders.Add(CsrfHeaderMiddleware.HeaderName, CsrfHeaderMiddleware.HeaderValue);
        (await client.RegisterAsync("owner@example.com")).EnsureSuccessStatusCode();
        var config = await client.GetFromJsonAsync<AuthConfigResponse>("/api/auth/config", Ct);
        config!.RegistrationOpen.ShouldBeFalse("appsettings.Desktop.json closes registration after the first user");
        File.Exists(Path.Combine(_directory, "data", "mailnix.db")).ShouldBeTrue();

        var exited = false;
        server.ExitedUnexpectedly += _ => exited = true;
        (await server.StopAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue("it stops by itself once its input closes");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60));
        exited.ShouldBeFalse();

        var log = await ReadLogAsync();
        log.ShouldContain("mailnix 0.0.0-dev");
        log.ShouldContain("Standard input closed, stopping");
    }

    [Fact]
    public async Task A_server_that_ends_by_itself_is_reported()
    {
        using var server = NewServer();
        await server.StartAsync(Ct);
        var exited = new TaskCompletionSource<int>();
        server.ExitedUnexpectedly += exited.SetResult;

        using (var process = Process.GetProcessById(server.ProcessId!.Value))
        {
            process.Kill();
        }

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        (await server.StopAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    /// <summary>The API's apphost from this build, standing in for mailnix.exe.</summary>
    private ServerProcess NewServer()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Mailnix.Api.exe" : "Mailnix.Api");
        return new ServerProcess(new ServerStartInfo(executable, Path.Combine(_directory, "data"), Ports.FindFree(), Path.Combine(_directory, "logs", "mailnix.log")));
    }

    private async Task<string> ReadLogAsync()
    {
        await using var stream = new FileStream(Path.Combine(_directory, "logs", "mailnix.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped.TrySetResult();
    }
}
