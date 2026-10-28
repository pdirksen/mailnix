using Mailnix.Desktop;

namespace Mailnix.Integration.Tests;

public sealed class DesktopUpdatesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mailnix-updates-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FakeUpdater(string current, string? pending = null) : IUpdater
    {
        public string CurrentVersion => current;

        public string? PendingVersion => pending;

        /// <summary>What the next check finds (null = nothing newer); an exception makes it fail.</summary>
        public Func<Task<string?>> Next { get; set; } = () => Task.FromResult<string?>(null);

        public List<string[]> Applied { get; } = [];

        public Task<string?> CheckAndDownloadAsync(CancellationToken ct) => Next();

        public void ApplyAndRestart(string[] restartArgs) => Applied.Add(restartArgs);
    }

    [Theory]
    [InlineData("https://mine.example/updates", "https://built.example/updates", "https://mine.example/updates")]
    [InlineData("  ", "https://built.example/updates", "https://built.example/updates")]
    [InlineData(null, @"\\server\share\updates", @"\\server\share\updates")]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void The_configured_feed_wins_over_the_built_in_one(string? configured, string? builtIn, string? expected) =>
        UpdateFeed.Resolve(configured, builtIn).ShouldBe(expected);

    [Fact]
    public void The_feed_can_be_set_in_the_data_folder()
    {
        var file = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(file, """{ "Mailnix": { "Desktop": { "UpdateUrl": "https://mine.example/updates" } } }""");
        DesktopSettings.Read(file).ShouldBe(new DesktopSettings(DesktopSettings.DefaultPort, "https://mine.example/updates"));

        File.WriteAllText(file, """{ "Mailnix": { "Desktop": { "Port": 5999, "UpdateUrl": 42 } } }""");
        DesktopSettings.Read(file).ShouldBe(new DesktopSettings(5999, null));
    }

    [Fact]
    public async Task Without_an_installed_app_or_a_feed_updates_are_off()
    {
        var updates = new DesktopUpdates(null);

        updates.State.ShouldBe(UpdateState.Unavailable);
        (await updates.CheckAsync(Ct)).Outcome.ShouldBe(UpdateOutcome.UpToDate);
        updates.Install([]);
    }

    [Fact]
    public async Task A_newer_version_is_downloaded_and_installed_on_request()
    {
        var updater = new FakeUpdater("1.4.0");
        var updates = new DesktopUpdates(updater);
        updates.State.ShouldBe(UpdateState.Idle);

        // Nothing newer: nothing to install.
        (await updates.CheckAsync(Ct)).ShouldBe((UpdateOutcome.UpToDate, null, null));
        updates.Install(["--background"]);
        updater.Applied.ShouldBeEmpty();

        updater.Next = () => Task.FromResult<string?>("1.5.0");
        (await updates.CheckAsync(Ct)).ShouldBe((UpdateOutcome.Ready, "1.5.0", null));
        updates.State.ShouldBe(UpdateState.Ready);
        updates.ReadyVersion.ShouldBe("1.5.0");

        // Another check finding nothing newer keeps the ready version.
        updater.Next = () => Task.FromResult<string?>(null);
        (await updates.CheckAsync(Ct)).ShouldBe((UpdateOutcome.Ready, "1.5.0", null));

        updates.Install(["--background", "--updated"]);
        updater.Applied.ShouldHaveSingleItem().ShouldBe(["--background", "--updated"]);
    }

    [Fact]
    public void An_update_downloaded_before_is_ready_at_once()
    {
        var updates = new DesktopUpdates(new FakeUpdater("1.4.0", pending: "1.5.0"));

        updates.State.ShouldBe(UpdateState.Ready);
        updates.ReadyVersion.ShouldBe("1.5.0");
    }

    [Fact]
    public async Task A_failed_check_is_reported_and_the_next_one_runs_again()
    {
        var updater = new FakeUpdater("1.4.0") { Next = () => throw new HttpRequestException("feed not found (404)") };
        var updates = new DesktopUpdates(updater);

        var (outcome, version, error) = await updates.CheckAsync(Ct);
        outcome.ShouldBe(UpdateOutcome.Failed);
        version.ShouldBeNull();
        error.ShouldBeOfType<HttpRequestException>().Message.ShouldContain("404");
        updates.State.ShouldBe(UpdateState.Idle);

        updater.Next = () => Task.FromResult<string?>("1.5.0");
        (await updates.CheckAsync(Ct)).Outcome.ShouldBe(UpdateOutcome.Ready);
    }

    [Fact]
    public async Task Only_one_check_runs_at_a_time()
    {
        var release = new TaskCompletionSource<string?>();
        var updater = new FakeUpdater("1.4.0") { Next = () => release.Task };
        var updates = new DesktopUpdates(updater);

        var first = updates.CheckAsync(Ct);
        updates.State.ShouldBe(UpdateState.Checking);
        (await updates.CheckAsync(Ct)).Outcome.ShouldBe(UpdateOutcome.UpToDate, "a second check while one runs returns at once");

        release.SetResult("1.5.0");
        (await first).Outcome.ShouldBe(UpdateOutcome.Ready);
        updates.State.ShouldBe(UpdateState.Ready);
    }
}
