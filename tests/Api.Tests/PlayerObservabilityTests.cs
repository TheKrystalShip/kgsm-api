using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="PlayerObservability"/> — the one answer to "can this host see who is on that server",
/// which both the roster endpoint's <c>detection</c> field and the online count on every server
/// element are built from.
/// <para>
/// What these lock is the refusal: every way of not knowing (no supervisor, an unreachable one, an
/// instance it does not mention, a game it cannot watch) has to come back unobservable, because the
/// caller turns that into an honest unknown and turns <em>observable</em> into a number that lands in
/// a fleet total.
/// </para>
/// </summary>
public sealed class PlayerObservabilityTests
{
    [Fact]
    public async Task A_watched_instance_is_observable_and_an_unwatched_one_is_not()
    {
        PlayerObservability obs = New(Presence(("factorio-1", "log"), ("rcon-game", "rcon"), ("blind", "none")));

        await obs.RefreshIfStaleAsync(default);

        Assert.True(obs.IsObservable("factorio-1"));
        Assert.True(obs.IsObservable("rcon-game"));
        Assert.False(obs.IsObservable("blind"));
    }

    // A capability the supervisor could not establish is not one that exists — "unknown" is the same
    // refusal as "none", and both must read as unobservable rather than as a roster worth counting.
    [Fact]
    public async Task An_unestablished_capability_is_not_observable()
    {
        PlayerObservability obs = New(Presence(("murky", "unknown")));

        await obs.RefreshIfStaleAsync(default);

        Assert.False(obs.IsObservable("murky"));
    }

    [Fact]
    public async Task An_instance_the_supervisor_never_mentions_is_not_observable()
    {
        PlayerObservability obs = New(Presence(("factorio-1", "log")));

        await obs.RefreshIfStaleAsync(default);

        Assert.False(obs.IsObservable("some-other-server"));
    }

    // No watchdog on this host at all — the honest answer is that presence cannot be seen, not that
    // every server is empty.
    [Fact]
    public async Task No_supervisor_is_not_observable()
    {
        var obs = new PlayerObservability(
            new ServiceCollection().BuildServiceProvider(), NullLogger<PlayerObservability>.Instance);

        await obs.RefreshIfStaleAsync(default);

        Assert.False(obs.IsObservable("factorio-1"));
    }

    // A supervisor that answers nothing must not leave a previous reading standing: a stale "yes" is
    // what would let a count be reported for a server nobody can see any more.
    [Fact]
    public async Task A_silent_supervisor_clears_the_previous_reading()
    {
        var watchdog = new FakePresenceWatchdog(Presence(("factorio-1", "log")));
        PlayerObservability obs = New(watchdog);

        await obs.RefreshIfStaleAsync(default);
        Assert.True(obs.IsObservable("factorio-1"));

        watchdog.Answer = null;
        await ForceRefreshAsync(obs);

        Assert.False(obs.IsObservable("factorio-1"));
    }

    // A throwing supervisor is the same refusal as a silent one, and it must not escape into the roster
    // build that called it.
    [Fact]
    public async Task A_failing_supervisor_is_swallowed_and_reads_as_unobservable()
    {
        var watchdog = new FakePresenceWatchdog(Presence(("factorio-1", "log"))) { Throw = true };
        PlayerObservability obs = New(watchdog);

        await obs.RefreshIfStaleAsync(default);

        Assert.False(obs.IsObservable("factorio-1"));
        Assert.Equal(1, watchdog.Calls);
    }

    // The reading is reused inside its TTL — a roster build every few seconds must not become a socket
    // call every few seconds, since detection changes with an instance's configuration, not with who is
    // playing.
    [Fact]
    public async Task The_reading_is_reused_within_its_ttl()
    {
        var watchdog = new FakePresenceWatchdog(Presence(("factorio-1", "log")));
        PlayerObservability obs = New(watchdog);

        await obs.RefreshIfStaleAsync(default);
        await obs.RefreshIfStaleAsync(default);
        await obs.RefreshIfStaleAsync(default);

        Assert.Equal(1, watchdog.Calls);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static PlayerObservability New(IReadOnlyDictionary<string, WatchdogInstancePresence> presence) =>
        New(new FakePresenceWatchdog(presence));

    private static PlayerObservability New(IWatchdogClient watchdog)
    {
        var services = new ServiceCollection();
        services.AddSingleton(watchdog);
        return new PlayerObservability(services.BuildServiceProvider(), NullLogger<PlayerObservability>.Instance);
    }

    private static Dictionary<string, WatchdogInstancePresence> Presence(params (string Id, string Detection)[] rows) =>
        rows.ToDictionary(r => r.Id, r => new WatchdogInstancePresence { Detection = r.Detection }, StringComparer.Ordinal);

    /// <summary>Take a second reading without waiting out the TTL in real time.</summary>
    private static Task ForceRefreshAsync(PlayerObservability obs)
    {
        typeof(PlayerObservability)
            .GetField("_readAtTicks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(obs, 0L);
        return obs.RefreshIfStaleAsync(default);
    }

    /// <summary>
    /// An <see cref="IWatchdogClient"/> that answers one canned presence map and counts how often it is
    /// asked. Every other member throws: this fake exists for the detection reading alone.
    /// </summary>
    private sealed class FakePresenceWatchdog(IReadOnlyDictionary<string, WatchdogInstancePresence>? answer)
        : IWatchdogClient
    {
        public IReadOnlyDictionary<string, WatchdogInstancePresence>? Answer { get; set; } = answer;
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> GetPlayerPresenceAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw) throw new IOException("the supervisor socket is gone");
            return Task.FromResult(Answer);
        }

        public void Dispose() { }

        // ---- unused by these tests ----
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> StartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> StopAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> BeginMaintenanceAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> EndMaintenanceAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> EnableAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> DisableAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetEnabledNamesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> ForgetAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> SetCpuPriorityAsync(string instanceName, string priority, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogActionResult> RestartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WatchdogRunTimes>> GetRunTimesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchdogRunTimes>>(Array.Empty<WatchdogRunTimes>());
        public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WatchdogConsoleRun>> GetConsoleRunsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> GetConsoleRunTailAsync(string instanceName, int run, int lines, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogConsoleWindow> GetConsoleWindowAsync(string instanceName, int lines, int run, long endOffset, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogConsoleDownload?> OpenConsoleDownloadAsync(string instanceName, int run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogUpnpList?> GetUpnpAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
