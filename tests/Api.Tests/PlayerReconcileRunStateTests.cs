using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The startup reconcile of the PERMANENT roster (<see cref="PlayerHistoryService.ReconcileFromWatchdogAsync"/>),
/// against a real SQLite file and a faked watchdog snapshot.
/// <para>
/// What these lock is the join the reconcile makes between two authorities: the watchdog's session map
/// (who is connected) and the engine's run-state (what is running). Believing the snapshot alone is
/// how a server that had been stopped for hours came back reporting two players online — the daemon's
/// map had outlived the process, and this method wrote its word into a durable record, where it then
/// survived every subsequent restart.
/// </para>
/// <para>
/// A real DB rather than the cache-only setup in <see cref="PlayerHistoryServiceTests"/>: the defect
/// was a persisted row, so a test that only inspected the in-memory cache would not have caught it.
/// </para>
/// </summary>
public sealed class PlayerReconcileRunStateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"kgsm-api-reconcile-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task A_stopped_servers_players_go_offline_even_when_the_watchdog_still_lists_them()
    {
        // The live defect, reproduced: projectzomboid stopped hours ago, the watchdog's map still names
        // the two players who were on it when it went down.
        await SeedAsync(("projectzomboid", "76561198144397568", PlayerStatus.online));

        InstanceCache instances = NewInstanceCache();
        instances.UpdateStatus("projectzomboid", running: false);
        PlayerHistoryService history = NewService(instances, Snapshot(("projectzomboid", "76561198144397568")));

        await history.ReconcileFromWatchdogAsync();

        RosterPlayer p = Assert.Single(history.GetRoster("projectzomboid"));
        Assert.Equal(PlayerStatus.offline, p.Status);
        Assert.Equal(PlayerStatus.offline, await StoredStatusAsync("projectzomboid", "76561198144397568"));
    }

    [Fact]
    public async Task A_running_servers_players_are_taken_from_the_snapshot()
    {
        // The contrast that pins the test above to the run-state join and not to a blanket refusal to
        // trust the watchdog: same snapshot, server running, player online.
        await SeedAsync(("factorio-1", "76561198144397568", PlayerStatus.offline));

        InstanceCache instances = NewInstanceCache();
        instances.UpdateStatus("factorio-1", running: true);
        PlayerHistoryService history = NewService(instances, Snapshot(("factorio-1", "76561198144397568")));

        await history.ReconcileFromWatchdogAsync();

        Assert.Equal(PlayerStatus.online, Assert.Single(history.GetRoster("factorio-1")).Status);
    }

    [Fact]
    public async Task An_unreadable_run_state_leaves_the_snapshot_believed()
    {
        // No reading at all (an engine this API cannot read). "We do not know" must not become "the
        // server is stopped" — that would be inventing a measurement to overrule a real one.
        await SeedAsync(("valheim-1", "player-a", PlayerStatus.offline));

        PlayerHistoryService history = NewService(NewInstanceCache(), Snapshot(("valheim-1", "player-a")));

        await history.ReconcileFromWatchdogAsync();

        Assert.Equal(PlayerStatus.online, Assert.Single(history.GetRoster("valheim-1")).Status);
    }

    [Fact]
    public async Task A_stopped_server_mints_no_row_for_a_player_the_roster_never_saw()
    {
        // Phase 2 of the reconcile discovers players the DB has never recorded. For a stopped server
        // that is not recovery, it is invention: a brand-new permanent row for someone who cannot be
        // connected to anything.
        InstanceCache instances = NewInstanceCache();
        instances.UpdateStatus("projectzomboid", running: false);
        PlayerHistoryService history = NewService(instances, Snapshot(("projectzomboid", "never-seen-before")));

        await history.ReconcileFromWatchdogAsync();

        Assert.Empty(history.GetRoster("projectzomboid"));
        using AppDbContext db = NewDbContext();
        Assert.Empty(db.PlayerHistory);
    }

    [Fact]
    public async Task With_no_watchdog_a_stopped_servers_players_go_offline_and_the_rest_unknown()
    {
        // The fallback path. Offline where a measurement says the server is down — nobody can be
        // connected to it — and unknown only where nothing is known, which is the honest answer for
        // events missed while this API was not listening.
        await SeedAsync(
            ("projectzomboid", "76561198144397568", PlayerStatus.online),
            ("factorio-1", "player-b", PlayerStatus.online));

        InstanceCache instances = NewInstanceCache();
        instances.UpdateStatus("projectzomboid", running: false);
        PlayerHistoryService history = NewService(instances, watchdog: null);

        await history.ReconcileFromWatchdogAsync();

        Assert.Equal(PlayerStatus.offline, Assert.Single(history.GetRoster("projectzomboid")).Status);
        Assert.Equal(PlayerStatus.unknown, Assert.Single(history.GetRoster("factorio-1")).Status);
    }

    [Fact]
    public async Task A_banned_player_on_a_stopped_server_stays_banned()
    {
        // Banned is an operator's decision, not an observation of presence — no reconcile branch may
        // overwrite it.
        await SeedAsync(("projectzomboid", "griefer", PlayerStatus.banned));

        InstanceCache instances = NewInstanceCache();
        instances.UpdateStatus("projectzomboid", running: false);
        PlayerHistoryService history = NewService(instances, Snapshot(("projectzomboid", "griefer")));

        await history.ReconcileFromWatchdogAsync();

        Assert.Equal(PlayerStatus.banned, Assert.Single(history.GetRoster("projectzomboid")).Status);
    }

    // ---- harness ---------------------------------------------------------------------------------

    private PlayerHistoryService NewService(InstanceCache instances, FakeWatchdog? watchdog)
    {
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        if (watchdog is not null)
            collection.AddSingleton<IWatchdogClient>(watchdog);
        ServiceProvider services = collection.BuildServiceProvider();

        return new PlayerHistoryService(
            services.GetRequiredService<IServiceScopeFactory>(),
            services,
            new StreamHub(Options.Create(new JsonOptions())),
            instances,
            NullLogger<PlayerHistoryService>.Instance);
    }

    private static InstanceCache NewInstanceCache()
    {
        IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ApiOptions options = ApiOptions.FromConfiguration(new ConfigurationBuilder().Build());
        return new InstanceCache(services, options, NullLogger<InstanceCache>.Instance);
    }

    private static FakeWatchdog Snapshot(params (string ServerId, string PlayerId)[] sessions)
    {
        var byInstance = new Dictionary<string, IReadOnlyList<WatchdogPlayer>>(StringComparer.Ordinal);
        foreach (var group in sessions.GroupBy(s => s.ServerId, StringComparer.Ordinal))
        {
            byInstance[group.Key] = [.. group.Select(s => new WatchdogPlayer
            {
                SessionKey = s.PlayerId,
                Id = s.PlayerId,
            })];
        }
        return new FakeWatchdog(byInstance);
    }

    private AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private async Task SeedAsync(params (string ServerId, string PlayerIdentity, PlayerStatus Status)[] rows)
    {
        using AppDbContext db = NewDbContext();
        await db.Database.EnsureCreatedAsync();
        DateTimeOffset seen = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var row in rows)
        {
            db.PlayerHistory.Add(new PlayerRecord
            {
                ServerId = row.ServerId,
                PlayerIdentity = row.PlayerIdentity,
                PlayerId = row.PlayerIdentity,
                Status = row.Status,
                FirstSeen = seen,
                LastSeen = seen,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<PlayerStatus> StoredStatusAsync(string serverId, string playerIdentity)
    {
        using AppDbContext db = NewDbContext();
        PlayerRecord row = await db.PlayerHistory.SingleAsync(
            p => p.ServerId == serverId && p.PlayerIdentity == playerIdentity);
        return row.Status;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// An <see cref="IWatchdogClient"/> that answers one canned session map. Every other member throws:
    /// the reconcile calls exactly one, and a test that starts depending on a second should say so.
    /// </summary>
    private sealed class FakeWatchdog(IReadOnlyDictionary<string, IReadOnlyList<WatchdogPlayer>> sessions)
        : IWatchdogClient
    {
        // The supervisor reports detection beside the sessions. These tests are about the run-state
        // join, so every instance they name is one it can observe — an undetectable one would be
        // testing a different refusal.
        public Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> GetPlayerPresenceAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, WatchdogInstancePresence>?>(
                sessions.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new WatchdogInstancePresence { Detection = "log", Players = [.. kvp.Value] },
                    StringComparer.Ordinal));

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

        // The console-run pair, stubbed like its sibling: this fake exists for run-state reconciliation
        // and is never asked about a console.
        public Task<IReadOnlyList<WatchdogConsoleRun>> GetConsoleRunsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetConsoleRunTailAsync(string instanceName, int run, int lines, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogConsoleWindow> GetConsoleWindowAsync(string instanceName, int lines, int run, long endOffset, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogConsoleDownload?> OpenConsoleDownloadAsync(string instanceName, int run, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WatchdogUpnpList?> GetUpnpAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
