using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// One instance's backup standing: its most recent snapshot and how many it holds. Both are honest-null
/// on <see cref="Unknown"/> — the cache has not read this instance yet, which is a different fact from
/// "this instance has no backups" (a read that returned zero, which is <c>Latest: null, Count: 0</c>).
/// Surfaces must keep those apart: the first renders as "unknown", the second as "none yet".
/// </summary>
/// <param name="Latest">The newest backup's full manifest record, or null when there are none / not read.</param>
/// <param name="Count">How many backups the instance holds, or null when not read.</param>
public sealed record BackupReading(ServerBackup? Latest, int? Count)
{
    /// <summary>The not-yet-read reading — both facts honestly absent, never a fabricated zero.</summary>
    public static readonly BackupReading Unknown = new(null, null);
}

/// <summary>
/// In-memory cache of each instance's backup standing, so <see cref="Contracts.Server.LastBackup"/> and
/// <see cref="Contracts.Server.BackupCount"/> can ride the server list without a per-request engine spawn.
/// Sits beside <see cref="InstanceCache"/> and <see cref="UpdateCheckCache"/> and follows the same shape: a
/// singleton <see cref="IHostedService"/> with a <see cref="PeriodicTimer"/>, a <see cref="SemaphoreSlim"/>
/// gate against concurrent refreshes, and a lock-free reference swap on read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cache rather than a per-request read.</b> Listing backups is a kgsm process spawn per instance,
/// far too expensive for the roster refresh that serves <c>GET /servers</c>. The relaxed cadence
/// (<see cref="ApiOptions.BackupScanPollMs"/>) carries the steady state, and
/// <see cref="RefreshInstanceAsync"/> covers the case that actually matters for freshness — a backup was
/// just taken or restored, so the engine event pokes the one affected instance immediately instead of
/// leaving the SPA to wait out the poll.
/// </para>
/// <para>
/// <b>An empty listing is only trusted when the engine said so.</b> kgsm-lib's
/// <c>GetBackupsDetailed</c> collapses a failed read and a genuinely empty store into the same empty list,
/// so it cannot carry that distinction on its own. The id-only <c>GetBackups</c> read runs first because
/// its <see cref="KgsmResult.IsSuccess"/> does: a failure keeps the prior reading (a transient engine blip
/// must never look like "the backups are gone"), while a success with no ids is a real, recorded zero.
/// An instance with no backups therefore costs one spawn, not two.
/// </para>
/// <para>
/// <b>Never wipes on a transient failure</b>, and the id set is owned by <see cref="InstanceCache.Roster"/>
/// — this cache only holds readings for instances the roster currently knows, so an uninstalled instance
/// drops out rather than lingering.
/// </para>
/// </remarks>
public sealed class BackupCache : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly InstanceCache _rosterCache;
    private readonly ILogger<BackupCache> _logger;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, BackupReading> _readings =
        new Dictionary<string, BackupReading>(StringComparer.Ordinal);
    private PeriodicTimer? _timer;

    // Latch so a persistent engine misconfiguration is logged once, not on every refresh.
    private int _engineUnavailableLogged;

    public BackupCache(IServiceProvider services, InstanceCache rosterCache, ApiOptions options,
        ILogger<BackupCache> logger)
    {
        _services = services;
        _rosterCache = rosterCache;
        _logger = logger;
        _interval = TimeSpan.FromMilliseconds(Math.Max(30_000, options.BackupScanPollMs));
    }

    /// <summary>
    /// The cached per-instance backup readings. Synchronous, lock-free read — safe on the hot path. A
    /// lookup miss is <see cref="BackupReading.Unknown"/>, not "no backups".
    /// </summary>
    public IReadOnlyDictionary<string, BackupReading> Readings => _readings;

    /// <summary>
    /// The reading for <paramref name="instanceId"/>, or <see cref="BackupReading.Unknown"/> when the cache
    /// holds no entry for it (cold cache, or an id outside the roster).
    /// </summary>
    public BackupReading Get(string instanceId) =>
        _readings.TryGetValue(instanceId, out BackupReading? r) ? r : BackupReading.Unknown;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(_interval);

        // A roster-wide scan is a spawn per instance; don't block startup on it. The first scan runs after
        // a short delay, so the API serves honest-null backup fields until it completes.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                await RefreshGuardedAsync("initial", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* app stopping */ }
        }, cancellationToken);

        _ = RunTimerAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshLock.Dispose();
    }

    private async Task RunTimerAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await RefreshGuardedAsync("background", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task RefreshGuardedAsync(string phase, CancellationToken ct)
    {
        if (!_refreshLock.Wait(0)) return; // a refresh is already in flight
        try { await RefreshAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "backup cache {Phase} refresh failed; will retry next cadence.", phase);
        }
        finally { _refreshLock.Release(); }
    }

    /// <summary>
    /// Trigger an immediate roster-wide refresh, awaiting completion. Returns <c>false</c> when a refresh is
    /// already in flight. The deterministic seam a test (or an operator "scan now") uses, serialized with the
    /// background loop.
    /// </summary>
    public async Task<bool> PollNowAsync(CancellationToken ct)
    {
        if (!_refreshLock.Wait(0)) return false;
        try { await RefreshAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "backup cache manual refresh failed.");
        }
        finally { _refreshLock.Release(); }
        return true;
    }

    /// <summary>
    /// Re-read ONE instance's backups out-of-band and merge the result in. Called from the kgsm event echo
    /// when a backup is created or restored, so the change is visible within a tick of the engine emitting
    /// it rather than at the next scan. Best-effort: a failed read leaves the prior reading in place.
    /// </summary>
    public async Task RefreshInstanceAsync(string instanceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;

        try
        {
            if (_services.GetService(typeof(IInstanceService)) is not IInstanceService instances)
                return;

            BackupReading? reading = await Task
                .Run(() => ReadInstance(instances, instanceId), ct).ConfigureAwait(false);
            if (reading is null) return; // engine read failed — keep whatever we had

            IReadOnlyDictionary<string, BackupReading> prior = _readings;
            _readings = new Dictionary<string, BackupReading>(prior, StringComparer.Ordinal)
            {
                [instanceId] = reading
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "backup cache refresh for {Instance} failed; keeping prior reading.", instanceId);
        }
    }

    /// <summary>
    /// Internal (not private) so the read → reading mapping can be exercised directly by tests without a
    /// live background tick. Production reaches this via the timer or <see cref="PollNowAsync"/>.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        if (_services.GetService(typeof(IInstanceService)) is not IInstanceService instances)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — backup cache stays empty.");
            return;
        }

        IReadOnlyDictionary<string, Instance> roster = _rosterCache.Roster;
        if (roster.Count == 0)
        {
            // A genuinely empty host, or the roster hasn't loaded yet. Either way there is nothing to hold
            // a reading for; the next tick populates once the roster is there.
            _readings = new Dictionary<string, BackupReading>(StringComparer.Ordinal);
            return;
        }

        IReadOnlyDictionary<string, BackupReading> prior = _readings;
        var next = new Dictionary<string, BackupReading>(StringComparer.Ordinal);

        foreach (string id in roster.Keys)
        {
            ct.ThrowIfCancellationRequested();

            BackupReading? reading = await Task.Run(() => ReadInstance(instances, id), ct).ConfigureAwait(false);

            // A failed read keeps the last known reading for that instance, or stays honestly unknown when
            // there is none — never a fabricated "no backups".
            next[id] = reading
                ?? (prior.TryGetValue(id, out BackupReading? p) ? p : BackupReading.Unknown);
        }

        _readings = next;
        _logger.LogDebug("backup cache refreshed: {Count} instance(s).", next.Count);
    }

    /// <summary>
    /// Read one instance's backup standing. Returns <see langword="null"/> when the engine read FAILED (the
    /// caller keeps the prior reading); a successful read of an empty store is a real
    /// <c>BackupReading(null, 0)</c>.
    /// </summary>
    private static BackupReading? ReadInstance(IInstanceService instances, string instanceId)
    {
        // The id-only listing runs first because its exit code distinguishes an engine failure from an empty
        // store; the detailed read collapses both to an empty list, so it cannot carry that signal alone.
        KgsmResult listed = instances.GetBackups(instanceId);
        if (!listed.IsSuccess)
            return null;

        string[] ids = (listed.Stdout ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (ids.Length == 0)
            return new BackupReading(null, 0); // measured zero — the instance genuinely holds no backups

        // Only now is the manifest read worth a second spawn.
        List<InstanceBackup> detailed = instances.GetBackupsDetailed(instanceId);

        ServerBackup? latest = detailed
            .Where(b => !string.IsNullOrWhiteSpace(b.Id))
            // Newest first, with un-dated manifests last: a backup whose manifest carries no creation time
            // cannot be shown to be the newest, so it never displaces one that can.
            .OrderByDescending(b => b.CreatedAt ?? DateTimeOffset.MinValue)
            .Select(ServerBackupMapping.FromManifest)
            .FirstOrDefault();

        // The id listing is the authority for how many backups exist — a backup whose manifest is missing or
        // unreadable still exists, and is still counted, even though it contributes no detail.
        return new BackupReading(latest, ids.Length);
    }
}
