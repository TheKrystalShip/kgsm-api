using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// In-memory cache for the per-instance update-check readings (the slow, networked probe). Sits beside
/// <see cref="InstanceCache"/>: that cache refreshes <c>GetAllStatuses(fast:true)</c> every ~60s (no
/// per-instance network hit), so its <c>Version.UpdatesAvailable</c> is always honestly null. This cache
/// runs the <c>fast:false</c> read on its own relaxed <see cref="ApiOptions.UpdateCheckPollMs"/> cadence
/// — the one that probes SteamCMD / per-game upstream version APIs — and serves the resulting
/// <c>{UpdatesAvailable, Latest, CheckedAt}</c> triple to <see cref="ServerAggregator.BuildServer"/> so
/// the <c>Server.UpdateAvailable</c>/<c>LatestVersion</c>/<c>UpdateCheckedAt</c> contract fields populate
/// honestly. Always-on (not subscriber-gated): <c>GET /servers</c> lights the SPA's Update chip with no
/// SSE client connected.
/// </summary>
/// <remarks>
/// <para>
/// Pattern mirrors <see cref="InstanceCache"/>: a hand-rolled singleton <see cref="IHostedService"/> with a
/// <see cref="SemaphoreSlim"/> gate preventing concurrent refreshes and a <see cref="PeriodicTimer"/>
/// driving the background loop. Read is a lock-free reference swap.
/// </para>
/// <para>
/// <b>Never wipes on a transient failure.</b> A failed/empty slow read keeps the prior snapshot — the
/// update-available flag must not flap off because the network blipped. An unprovisioned engine yields an
/// empty cache (the same degrade as <see cref="InstanceCache"/>).</para>
/// <para>
/// <b>The id set is owned by <see cref="InstanceCache.Roster"/>.</b> Instances enter/leave the host via the
/// roster; this cache only holds readings for ids the roster currently knows. The slow status dict's keys
/// are cross-referenced against the roster so a wholesale read failure (kgsm-lib returns <c>[]</c> on a
/// non-empty roster) is detected and falls back to the prior snapshot rather than wiping every reading.
/// </para>
/// <para>
/// <b>Per-instance soft failures keep the prior reading too.</b> A measured reading whose
/// <c>Version.Checked</c> is false (the probe ran but couldn't determine — e.g. an upstream API timeout) or
/// an <c>Unavailable</c> reading (the per-instance status read itself failed) both fall back to the last
/// known reading for that id, or to honest-<c>null</c> <see cref="UpdateReading.Unknown"/> when none exists.
/// Never fabricated.
/// </para>
/// <para>
/// <b>Doesn't block startup.</b> A slow-mode check can take minutes (SteamCMD / factorio.com); <see cref="StartAsync"/>
/// just starts the timer. The first check runs in the loop after a short delay, so the cache is empty
/// (all readings <see cref="UpdateReading.Unknown"/>) until it completes — exactly today's behavior. The
/// 10-min cadence keeps upstream API pressure gentle while the SPA surfaces light within minutes of a release.
/// </para>
/// </remarks>
public sealed class UpdateCheckCache : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly InstanceCache _rosterCache;
    private readonly ILogger<UpdateCheckCache> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _disabled;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, UpdateReading> _readings = new Dictionary<string, UpdateReading>();
    private PeriodicTimer? _timer;
    private CancellationToken _stoppingToken = CancellationToken.None;

    // Latch so a persistent engine misconfiguration is logged once, not on every refresh.
    private int _engineUnavailableLogged;

    public UpdateCheckCache(IServiceProvider services, InstanceCache rosterCache, ApiOptions options,
        ILogger<UpdateCheckCache> logger)
    {
        _services = services;
        _rosterCache = rosterCache;
        _logger = logger;
        _interval = TimeSpan.FromMilliseconds(Math.Max(60_000, options.UpdateCheckPollMs));
        _disabled = options.UpdateCheckDisabled;
    }

    /// <summary>
    /// The cached per-instance update-check readings. Synchronous, lock-free read — safe on the hot path.
    /// Returns an empty dictionary until the first refresh completes; a lookup miss is <em>not</em> an
    /// "unchecked" instance — callers should treat a missing key as <see cref="UpdateReading.Unknown"/>.
    /// </summary>
    public IReadOnlyDictionary<string, UpdateReading> Readings => _readings;

    /// <summary>
    /// Mark an instance as having no update available immediately after a successful version change.
    /// The stale <c>UpdatesAvailable=true</c> reading from the last slow probe is voided so the
    /// verify <c>server.patch</c> built right after this call carries the cleared state. Called by
    /// <see cref="Commands.CommandRunner"/> on successful update verb, and by
    /// <see cref="Audit.KgsmAuditConsumer"/> on the kgsm <c>instance_version_updated</c> event
    /// (covers CLI-driven updates outside the API). The next 10-min slow poll re-probes honestly;
    /// this is a best-effort cache-priming call that prevents the SPA from seeing stale
    /// "update available" state for up to 10 minutes after the update landed.
    /// </summary>
    public void MarkUpdated(string instanceId)
    {
        IReadOnlyDictionary<string, UpdateReading> prior = _readings;
        if (!prior.TryGetValue(instanceId, out UpdateReading? old))
        {
            // Instance not in the cache yet — add it as "no update available" (we just confirmed
            // one by applying it). The next slow poll will populate the full reading.
            _readings = new Dictionary<string, UpdateReading>(prior, StringComparer.Ordinal)
            {
                [instanceId] = new UpdateReading(false, null, DateTimeOffset.UtcNow)
            };
            return;
        }
        // Replace the stale reading: UpdatesAvailable=false (the update just landed), keep the
        // prior LatestVersion (still the correct target as of last check), CheckedAt=now (we
        // just confirmed the state — not fabricated, just refreshed on the spot).
        _readings = new Dictionary<string, UpdateReading>(prior, StringComparer.Ordinal)
        {
            [instanceId] = new UpdateReading(false, old.LatestVersion, DateTimeOffset.UtcNow)
        };
    }

    /// <summary>
    /// Get the reading for <paramref name="instanceId"/>, or <see cref="UpdateReading.Unknown"/> when the
    /// cache has no entry (the cache is cold, or the id is not in the roster). The honest-null triple —
    /// never fabricated.
    /// </summary>
    public UpdateReading Get(string instanceId) =>
        _readings.TryGetValue(instanceId, out UpdateReading? r) ? r : UpdateReading.Unknown;

    /// <summary>
    /// Trigger an immediate refresh (out-of-band), awaiting its completion. Returns <c>false</c> if a
    /// refresh is already in flight (the background timer or the initial delayed refresh). Mirrors
    /// <see cref="Realtime.LeafHealthMonitor"/>'s <c>PollNowAsync</c> seam — the deterministic hook a test
    /// (or a future "check now" operator endpoint) uses to force a fresh slow read on demand, serialized
    /// with the background loop under <see cref="_refreshLock"/>.
    /// </summary>
    public async Task<bool> PollNowAsync(CancellationToken ct)
    {
        if (!_refreshLock.Wait(0)) return false;
        try { await RefreshAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "update-check cache manual refresh failed.");
        }
        finally { _refreshLock.Release(); }
        return true;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        if (_disabled)
        {
            _logger.LogInformation("update-check cache: disabled (Api__UpdateCheckDisabled) — probe inert, fields stay null.");
            return;
        }
        _timer = new PeriodicTimer(_interval);

        // A slow-mode check can take minutes (SteamCMD / factorio.com); do NOT block startup. The first
        // check runs after a short delay so the API is serving requests (honest-null readings, today's
        // behavior) while the probe is still in flight.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (!_refreshLock.Wait(0)) return;
                try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "update-check cache initial refresh failed; will retry next cadence.");
                }
                finally { _refreshLock.Release(); }
            }
            catch (OperationCanceledException) { /* app stopping */ }
        }, cancellationToken);

        // Background timer loop — runs for the process lifetime.
        _ = RunTimerAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    private async Task RunTimerAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_refreshLock.Wait(0)) continue; // skip if a refresh is already in flight
                try { await RefreshAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "update-check cache background refresh failed; will retry next cadence.");
                }
                finally { _refreshLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    /// <summary>
    /// Internal (not private) so the slow-read → reading mapping can be exercised directly by tests with a
    /// synthetic "fresh slow read" — the most direct way to prove the honest-null / keep-prior rules without
    /// a live background tick. Production only reaches this via <see cref="RunTimerAsync"/>.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        var instances = _services.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (Api__KgsmPath is empty) — update-check cache stays empty.");
            return;
        }

        // The roster is the authority for the id set; never trust the slow status dict's keys alone
        // (a wholesale read failure collapses it to []). Read lock-free — the same reference InstanceCache
        // serves every other consumer.
        IReadOnlyDictionary<string, Instance> roster = _rosterCache.Roster;
        if (roster.Count == 0)
        {
            // Genuine empty host OR the roster cache hasn't loaded yet. Either way, keep whatever readings
            // we have — the roster may load on the next tick and we'll populate then.
            _readings = new Dictionary<string, UpdateReading>();
            return;
        }

        Dictionary<string, Reading<InstanceRuntimeStatus>> slowStatuses;
        try
        {
            // fast:false — the networked per-instance probe. This is the slow path (~seconds to minutes).
            slowStatuses = await Task.Run(() => instances.GetAllStatuses(fast: false), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "update-check refresh: slow status read threw — keeping {Count} prior reading(s).",
                _readings.Count);
            return;
        }

        // Wholesale failure: kgsm-lib returns [] on a non-zero exit. A non-empty roster + empty slow read
        // is a read failure, not "every instance lost its version" — keep the prior snapshot.
        if (slowStatuses.Count == 0)
        {
            _logger.LogWarning(
                "update-check refresh: slow status read returned no instances for a {Count}-instance roster "
                + "(transient read failure) — keeping prior reading(s).", roster.Count);
            return;
        }

        IReadOnlyDictionary<string, UpdateReading> prior = _readings;
        var next = new Dictionary<string, UpdateReading>(StringComparer.Ordinal);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (string id in roster.Keys)
        {
            if (slowStatuses.TryGetValue(id, out Reading<InstanceRuntimeStatus>? reading)
                && reading is { IsMeasured: true, Value: { } status }
                && status.Version.Checked)
            {
                // A real, checked reading — record it. UpdatesAvailable/Latest are nullable themselves
                // (null when Checked is false, but we've gated on Checked above; still honest null when the
                // engine couldn't determine Latest even though it ran the probe).
                next[id] = new UpdateReading(status.Version.UpdatesAvailable, status.Version.Latest, now);
            }
            else if (prior.TryGetValue(id, out UpdateReading? priorReading))
            {
                // Soft per-instance failure (Checked=false, or an Unavailable reading) — keep the last
                // known reading. A transient upstream API blip must not flip the Update chip off.
                next[id] = priorReading;
            }
            else
            {
                // Never checked yet — the honest-null triple.
                next[id] = UpdateReading.Unknown;
            }
        }

        _readings = next;
        _logger.LogDebug("update-check cache refreshed: {Count} instance(s).", next.Count);

        // Emit audit events for newly-detected updates (state transition: false/null → true).
        // Fire-and-forget: emit failure is logged and swallowed, never crashes the refresh.
        _ = Task.Run(async () =>
        {
            try
            {
                var audit = _services.GetService(typeof(AuditService)) as AuditService;
                if (audit is null) return;

                var instanceCache = _services.GetService(typeof(InstanceCache)) as InstanceCache;
                string hostId = _services.GetRequiredService<ApiOptions>().HostId;

                foreach (string id in next.Keys)
                {
                    if (next.TryGetValue(id, out UpdateReading? nr) && nr.UpdatesAvailable == true
                        && (!prior.TryGetValue(id, out UpdateReading? pr) || pr.UpdatesAvailable != true))
                    {
                        // State transition: update just became available. Pull the current version
                        // from the instance cache (the fast-mode status reading) so the audit trail
                        // records the exact "from → to" version pair at detection time.
                        string? currentVersion = instanceCache?.Statuses.TryGetValue(id, out var s) == true
                            ? s.Value?.Version.Current
                            : null;

                        var write = AuditMapping.FromUpdateAvailable(id, currentVersion, nr.LatestVersion, hostId);
                        await audit.AppendAsync(write).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "update-check cache: failed to emit update-available audit events.");
            }
        });
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshLock.Dispose();
    }
}

/// <summary>
/// One instance's update-check reading: the honest-null-able triple kgsm's slow status read populates.
/// <see cref="Unknown"/> is the cold/unchecked reading — never fabricated.
/// </summary>
public sealed record UpdateReading(bool? UpdatesAvailable, string? LatestVersion, DateTimeOffset? CheckedAt)
{
    /// <summary>The honest-null triple: never checked (cold cache, or a per-instance failure with no prior).</summary>
    public static UpdateReading Unknown => new(null, null, null);
}