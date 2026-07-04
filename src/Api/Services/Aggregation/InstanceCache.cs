using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// In-memory cache for the kgsm instance roster + run-state. Sits between consumers
/// (<see cref="ServerAggregator"/>, <see cref="Realtime.DomainPump"/>, <see cref="NetworkAggregator"/>)
/// and the kgsm engine's <see cref="IInstanceService"/> — two synchronous process spawns (~100ms each)
/// that would otherwise run on every <c>GET /servers</c> request, every DomainPump tick, and every
/// command-verify read.
/// </summary>
/// <remarks>
/// <para>
/// Pattern follows <see cref="Library.BlueprintCache"/>: a hand-rolled singleton
/// <see cref="IHostedService"/> with a <see cref="SemaphoreSlim"/> gate preventing concurrent refreshes.
/// A background <see cref="PeriodicTimer"/> refreshes every <see cref="ApiOptions.InstanceCacheTtlSeconds"/>
/// seconds; the first call triggers a synchronous initial load so the cache is populated before the first
/// request arrives.
/// </para>
/// <para>
/// Between background refreshes, kgsm lifecycle events (via <see cref="Audit.KgsmAuditConsumer"/>)
/// update the runtime status in-place — started/stopped/restarted/crashed/failed flip the
/// <see cref="Reading{T}"/> state without a process spawn. Install/uninstall events trigger an immediate
/// full refresh via <see cref="ScheduleRefresh"/>. The 60-second background refresh is the reconciliation
/// point that re-declares authoritative truth and fills in fields events cannot carry (PID, start time,
/// version, etc.).
/// </para>
/// <para>
/// On refresh failure the stale data is kept — a transient kgsm failure must not wipe the roster
/// (instances don't disappear from a transient read failure). An engine that was never configured
/// yields an empty cache — the same degrade as today.
/// </para>
/// <para>
/// <b>The "starting" latch (tri-state run-state) and the reconcile hazard.</b> kgsm's boolean
/// run-state (<see cref="Reading{T}"/>'s <c>Status</c>) can only say "process up" or "process down" —
/// it cannot distinguish a process that just spawned (<c>instance_started</c>) from one the watchdog
/// has confirmed finished booting (<c>instance_ready</c>), because both observe the process as up. So
/// that distinction is tracked <em>separately</em> here, as a small set of instance names currently
/// inside their post-start "booting" window (<see cref="MarkStarting"/> opens it,
/// <see cref="MarkReady"/> or a stop/crash/fail closes it — see <see cref="UpdateStatus"/>).
/// <see cref="IsStarting"/> is what <c>ServerAggregator.BuildServer</c> consults to fold the latch into
/// the DTO's <c>starting</c> status.
/// </para>
/// <para>
/// The background boolean reconcile (<see cref="RefreshAsync"/>, every <see cref="_ttl"/>) is the
/// hazard: while an instance is genuinely still starting, the process IS up, so
/// <c>GetAllStatuses(fast:true)</c> reports <c>Status:true</c> — identical to a fully running instance.
/// The reconcile must NOT be allowed to treat that as evidence the instance is done starting (it would
/// silently flip <c>starting → running</c> before <c>instance_ready</c> ever arrives, defeating the
/// whole feature). <see cref="ReconcileStartingLatch"/> is therefore called on every refresh and only
/// ever <em>closes</em> the latch on genuinely new evidence: the fresh read shows the process is DOWN
/// (a silent death with no crash/stop event), the instance vanished from the roster, or the window has
/// run past a safety timeout (<see cref="StartingTimeout"/>, 5 minutes — generous for a slow modpack
/// boot, but bounded so a dropped/never-emitted <c>instance_ready</c> can't wedge an instance in
/// "starting" forever; on timeout it resolves honestly to <c>running</c>, since that IS what the same
/// reconcile pass just measured — never a fabricated state). It never flips <c>starting → running</c>
/// on a still-up, still-within-timeout read; only <see cref="MarkReady"/> does that.
/// </para>
/// </remarks>
public sealed class InstanceCache : IHostedService, IDisposable
{
    /// <summary>
    /// Safety bound on the "starting" window (see the class remarks). Chosen generously — most games
    /// boot in seconds, but a large modpack/world-gen can take minutes — while still bounding how long
    /// a dropped <c>instance_ready</c> can leave a server display-stuck on "starting". On expiry the
    /// instance resolves to <c>running</c> (the process is observed up in that same reconcile pass),
    /// never to <c>unknown</c> — the process state IS known, only the "finished booting" signal is late.
    /// </summary>
    internal static readonly TimeSpan StartingTimeout = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly ILogger<InstanceCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, Instance> _roster = new Dictionary<string, Instance>();
    private IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> _statuses =
        new Dictionary<string, Reading<InstanceRuntimeStatus>>();
    private bool _engineRead = true;
    private PeriodicTimer? _timer;
    private CancellationToken _stoppingToken = CancellationToken.None;

    // The starting latch: instance name -> when it entered the window. A SEPARATE lock from _statuses
    // (guarded below) — conceptually independent state, deliberately not folded into the status Reading
    // itself (that dictionary is wholesale-replaced by RefreshAsync; this one needs its own reconcile pass).
    private readonly object _startingGate = new();
    private readonly Dictionary<string, DateTimeOffset> _startingSince = new(StringComparer.Ordinal);

    // Latch so a persistent engine misconfiguration is logged once, not on every refresh.
    private int _engineUnavailableLogged;

    public InstanceCache(IServiceProvider services, ApiOptions options, ILogger<InstanceCache> logger)
    {
        _services = services;
        _logger = logger;
        _ttl = TimeSpan.FromSeconds(Math.Max(10, options.InstanceCacheTtlSeconds));
    }

    /// <summary>
    /// The cached instance roster. Synchronous, lock-free read — safe on the hot path.
    /// Returns an empty dictionary until the first refresh completes.
    /// </summary>
    public IReadOnlyDictionary<string, Instance> Roster => _roster;

    /// <summary>
    /// The cached per-instance run-state readings. Synchronous, lock-free read.
    /// Returns an empty dictionary until the first refresh completes.
    /// </summary>
    public IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> Statuses => _statuses;

    /// <summary>
    /// Whether the last background refresh successfully read the engine. Callers that must distinguish
    /// "couldn't read" from "genuinely empty" (the 503/skip-tick decision) check this.
    /// </summary>
    public bool EngineRead => _engineRead;

    /// <summary>
    /// Trigger an immediate, non-blocking refresh. Returns <c>false</c> if a refresh is already in
    /// flight (the background timer or a prior manual trigger). Used by event handlers on
    /// install/uninstall to reconcile the roster immediately.
    /// </summary>
    public bool TryRefresh()
    {
        if (!_refreshLock.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(_stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Instance cache manual refresh failed.");
            }
            finally { _refreshLock.Release(); }
        });
        return true;
    }

    /// <summary>
    /// Update an instance's runtime status in-place from a kgsm lifecycle event. Preserves the
    /// existing reading's version/process/resource data when present; creates a minimal measured
    /// reading when none exists yet. This is the event-driven fast path — no process spawn.
    /// </summary>
    public void UpdateStatus(string instanceName, bool running)
    {
        if (string.IsNullOrEmpty(instanceName)) return;

        lock (_statuses)
        {
            var mutable = new Dictionary<string, Reading<InstanceRuntimeStatus>>(
                (Dictionary<string, Reading<InstanceRuntimeStatus>>)_statuses, StringComparer.Ordinal);

            if (mutable.TryGetValue(instanceName, out Reading<InstanceRuntimeStatus>? existing)
                && existing is { IsMeasured: true, Value: { } value })
            {
                // Preserve the existing reading's data; flip only the status bool.
                mutable[instanceName] = Reading<InstanceRuntimeStatus>.Measured(
                    value with { Status = running });
            }
            else
            {
                // No existing measured reading — create a minimal one with just the status.
                mutable[instanceName] = Reading<InstanceRuntimeStatus>.Measured(
                    new InstanceRuntimeStatus { InstanceName = instanceName, Status = running });
            }

            _statuses = mutable;
        }

        // Going down always closes any open starting window — a stop/crash/failed instance is not
        // "still booting" by definition, regardless of how it got there. (A running->running flip, e.g.
        // the background reconcile re-affirming an already-settled instance, leaves the latch alone —
        // only MarkStarting opens it.)
        if (!running)
            ClearStartingLatch(instanceName);

        _logger.LogDebug("Instance cache: {Instance} status updated to {Status} (event-driven).",
            instanceName, running ? "running" : "stopped");
    }

    /// <summary>
    /// Event-driven: <c>instance_started</c>. The process has spawned — flips the boolean status to
    /// running (same as before) AND opens the "starting" window, so <c>ServerAggregator.BuildServer</c>
    /// reports <c>starting</c>, not <c>running</c>, until <see cref="MarkReady"/> (or a stop/crash/fail)
    /// closes it. See the class remarks for the full tri-state design + the reconcile hazard.
    /// </summary>
    public void MarkStarting(string instanceName) => MarkStartingAt(instanceName, DateTimeOffset.UtcNow);

    /// <summary>
    /// Same as <see cref="MarkStarting"/> but with an explicit "entered the window at" timestamp — the
    /// test seam for <see cref="StartingTimeout"/> expiry (so a test can seed an already-expired window
    /// deterministically instead of manipulating the wall clock or a shared static). Production code
    /// should call <see cref="MarkStarting"/>.
    /// </summary>
    internal void MarkStartingAt(string instanceName, DateTimeOffset since)
    {
        if (string.IsNullOrEmpty(instanceName)) return;

        UpdateStatus(instanceName, running: true);

        lock (_startingGate)
            _startingSince[instanceName] = since;

        _logger.LogDebug("Instance cache: {Instance} entered the starting window (event-driven).", instanceName);
    }

    /// <summary>
    /// Event-driven: <c>instance_ready</c> — the watchdog confirms the game finished booting. Closes the
    /// starting window, so the next read reports <c>running</c>. Defensive: if no window was open (e.g.
    /// this consumer wasn't listening for the earlier <c>instance_started</c>, or the instance was never
    /// marked starting), a ready event is still real evidence the process is up, so the boolean status is
    /// flipped to running too — never left stale.
    /// </summary>
    public void MarkReady(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName)) return;

        bool wasStarting;
        lock (_startingGate)
            wasStarting = _startingSince.Remove(instanceName);

        if (!wasStarting)
            UpdateStatus(instanceName, running: true);

        _logger.LogDebug("Instance cache: {Instance} left the starting window -> running (event-driven).",
            instanceName);
    }

    /// <summary>
    /// Whether <paramref name="instanceName"/> is currently inside its post-start "booting" window.
    /// Consulted by <c>ServerAggregator.BuildServer</c> to fold the latch into the DTO's <c>status</c> —
    /// the latch only ever matters when the underlying boolean reading is already "up" (see BuildServer).
    /// </summary>
    public bool IsStarting(string instanceName)
    {
        lock (_startingGate)
            return _startingSince.ContainsKey(instanceName);
    }

    private void ClearStartingLatch(string instanceName)
    {
        lock (_startingGate)
            _startingSince.Remove(instanceName);
    }

    /// <summary>
    /// The reconcile-hazard guard (see the class remarks): called from every background/manual refresh
    /// with the FRESH boolean statuses just read from the engine. Only ever CLOSES a starting window —
    /// never the mechanism that promotes <c>starting -> running</c> on a still-up read (that is
    /// <see cref="MarkReady"/>'s job alone). A window closes here only on new, honest evidence: the
    /// engine now reports the instance measured-down (a silent death with no crash/stop event reached
    /// us), the instance vanished from the roster entirely, or it has run past <see cref="StartingTimeout"/>.
    /// </summary>
    /// <summary>Internal (not private) so the reconcile-hazard guard can be exercised directly by tests
    /// with a synthetic "fresh read" — the most direct way to prove it, without needing a live background
    /// timer tick. Production code only ever reaches this via <see cref="RefreshAsync"/>.</summary>
    internal void ReconcileStartingLatch(IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> freshStatuses)
    {
        lock (_startingGate)
        {
            if (_startingSince.Count == 0) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<string>? toClose = null;
            foreach ((string id, DateTimeOffset since) in _startingSince)
            {
                bool measuredDown = freshStatuses.TryGetValue(id, out Reading<InstanceRuntimeStatus>? r)
                    && r is { IsMeasured: true, Value.Status: false };
                bool vanished = !freshStatuses.ContainsKey(id);
                bool timedOut = now - since > StartingTimeout;

                if (!measuredDown && !vanished && !timedOut)
                    continue; // still legitimately starting — the boolean-true reconcile must not win here

                (toClose ??= new List<string>()).Add(id);

                if (timedOut && !measuredDown && !vanished)
                    _logger.LogInformation(
                        "Instance cache: {Instance} exceeded the {Timeout} starting-window safety bound "
                        + "with the process still observed up — resolving to running honestly (never "
                        + "stuck 'starting' forever; instance_ready may simply not have been delivered).",
                        id, StartingTimeout);
            }

            if (toClose is not null)
                foreach (string id in toClose)
                    _startingSince.Remove(id);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        _timer = new PeriodicTimer(_ttl);

        // Initial refresh — synchronous so the cache is populated before the first request arrives.
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
        finally { _refreshLock.Release(); }

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
                    _logger.LogWarning(ex, "Instance cache background refresh failed; will retry next cadence.");
                }
                finally { _refreshLock.Release(); }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var instances = _services.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — instance cache stays empty.");
            _engineRead = true; // honest empty roster, not a failed read
            return;
        }

        try
        {
            // GetAllOrNull distinguishes a FAILED read (null) from a genuine empty roster.
            // A failed read must NOT replace the cache — keep stale data.
            Dictionary<string, Instance>? roster = await Task.Run(() => instances.GetAllOrNull(), ct)
                .ConfigureAwait(false);
            if (roster is null)
            {
                _logger.LogWarning(
                    "Instance cache refresh: kgsm instance-roster read failed — keeping stale data.");
                _engineRead = false;
                return;
            }

            // fast: skip the per-instance network update-check (~20x faster).
            Dictionary<string, Reading<InstanceRuntimeStatus>> statuses =
                await Task.Run(() => instances.GetAllStatuses(fast: true), ct).ConfigureAwait(false);

            // MUST run against this fresh read, BEFORE it becomes _statuses — see the reconcile-hazard
            // remarks on the class and on ReconcileStartingLatch itself. It only ever closes a starting
            // window on new evidence; it never promotes starting -> running on a still-up read.
            ReconcileStartingLatch(statuses);

            _roster = roster;
            _statuses = statuses;
            _engineRead = true;

            _logger.LogDebug("Instance cache refreshed: {Count} instance(s).", roster.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // On failure keep stale data — a transient kgsm failure must not wipe the roster.
            _logger.LogWarning(ex, "Instance cache refresh failed; keeping stale data ({Count} instance(s)).",
                _roster.Count);
            _engineRead = false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _refreshLock.Dispose();
    }
}
