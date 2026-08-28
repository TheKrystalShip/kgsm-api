using System.Globalization;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.Api.Services.Players;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Builds this host's <see cref="Server"/> list (architecture §3) for the M1·b read surface — the
/// project's central join: the kgsm engine's domain + run-state (from <see cref="InstanceCache"/>)
/// ⋈ the per-instance metrics (via kgsm-monitor), keyed on the instance id. The roster is served
/// from the in-memory cache (updated by events + a 60s background refresh); metrics are looked up
/// by id in the monitor snapshot. A server with no metrics row (monitor absent, or simply not
/// running) gets <c>metrics: null</c> — never a fabricated zero (the honesty invariant).
/// </summary>
/// <remarks>
/// The instance cache eliminates per-request process spawns — the roster + run-state are read from
/// memory (lock-free reference swap) instead of shelling <c>kgsm.sh</c> on every call. The monitor
/// scrape (a socket read, not a process spawn) remains on-demand and concurrent.
/// </remarks>
public sealed class ServerAggregator
{
    private readonly ApiOptions _options;
    private readonly MonitorClient _monitor;
    private readonly NetworkAggregator _network;
    private readonly RawgStore _rawg;
    private readonly InstanceCache _cache;
    private readonly BackupCache _backups;
    private readonly Commands.JobRegistry _jobs;
    private readonly PlayerHistoryService _players;
    private readonly PlayerObservability _observability;
    private readonly Availability.UpdateLagIndex _updateLag;
    private readonly Availability.RunTimesIndex _runTimes;
    private readonly Availability.SupervisionPhaseIndex _phases;
    private readonly Library.BlueprintCache _blueprints;
    private readonly ILogger<ServerAggregator> _logger;

    public ServerAggregator(
        ApiOptions options,
        MonitorClient monitor,
        NetworkAggregator network,
        RawgStore rawg,
        InstanceCache cache,
        BackupCache backups,
        Commands.JobRegistry jobs,
        PlayerHistoryService players,
        PlayerObservability observability,
        Availability.UpdateLagIndex updateLag,
        Availability.RunTimesIndex runTimes,
        Availability.SupervisionPhaseIndex phases,
        Library.BlueprintCache blueprints,
        ILogger<ServerAggregator> logger)
    {
        _options = options;
        _monitor = monitor;
        _network = network;
        _rawg = rawg;
        _cache = cache;
        _backups = backups;
        _jobs = jobs;
        _players = players;
        _observability = observability;
        _updateLag = updateLag;
        _runTimes = runTimes;
        _phases = phases;
        _blueprints = blueprints;
        _logger = logger;
    }

    /// <summary>
    /// How many people are connected to one instance, or null when this host cannot see who is on it.
    /// The count comes off the same roster <c>GET /servers/{id}/players</c> serves, so a card and a fleet
    /// total read one number; observability is the supervisor's answer, cached. Static shape (a
    /// <c>Func&lt;string,int?&gt;</c>) so <see cref="Realtime.DomainPump"/> composes the identical rule.
    /// </summary>
    internal static Func<string, int?> OnlinePlayersOf(PlayerHistoryService players, PlayerObservability observability) =>
        id => observability.IsObservable(id) ? players.OnlineCount(id) : null;

    /// <summary>
    /// A blueprint's advisory <c>min_ram_mb</c>, or null when it declares none — the fallback half of
    /// what a start is expected to cost.
    /// </summary>
    /// <remarks>
    /// Read from the blueprint cache rather than the engine, so publishing this on every server costs a
    /// dictionary lookup instead of a kgsm invocation per row. Static and shaped as a Func for the same
    /// reason <see cref="OnlinePlayersOf"/> is: <see cref="Realtime.DomainPump"/> composes the identical
    /// rule, and a stream frame that disagreed with the REST read about a server's requirement would be
    /// two answers to one question.
    /// </remarks>
    internal static Func<string, int?> BlueprintMinRamOf(Library.BlueprintCache blueprints) =>
        blueprint => blueprints.GetAll().TryGetValue(blueprint, out Blueprint? bp)
            ? bp.Metadata?.MinRamMb
            : null;

    /// <summary>
    /// Build the full server list for this host AND report whether the engine was actually read. A
    /// transient kgsm read failure surfaces as <see cref="ServersRead.EngineRead"/> == false with an
    /// empty list — so a caller that must not mistake "couldn't read" for "zero servers" (the
    /// <c>GET /servers</c> endpoint → 503-keep-stale; the <see cref="Realtime.DomainPump"/> → skip the
    /// tick) can tell the two apart. A successful read of a genuinely empty roster is
    /// <c>EngineRead == true</c> with an empty list (honestly zero servers).
    /// </summary>
    public async Task<ServersRead> GetServersReadAsync(CancellationToken ct)
    {
        if (!_cache.EngineRead)
            return new ServersRead(false, []);

        // Monitor scrape (a socket read, not a process spawn) runs concurrently with the sync cache read,
        // and so does the supervisor's presence reading — both are local sockets, and the observability
        // one is TTL-gated, so most builds take neither.
        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        Task observabilityTask = _observability.RefreshIfStaleAsync(ct);
        await Task.WhenAll(snapshotTask, observabilityTask).ConfigureAwait(false);

        IReadOnlyList<Server> servers = Join(
            _cache.Roster, _cache.Statuses, _backups.Readings, snapshotTask.Result);
        return new ServersRead(true, servers);
    }

    /// <summary>
    /// Build the full server list for this host (the lenient read used by existence checks and pumps):
    /// a failed engine read collapses to an empty list, exactly as before. Surfaces that must
    /// distinguish a failed read from a genuine empty roster use <see cref="GetServersReadAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<Server>> GetServersAsync(CancellationToken ct) =>
        (await GetServersReadAsync(ct).ConfigureAwait(false)).Servers;

    /// <summary>
    /// Build one server's <strong>detail</strong> record (the <c>GET /servers/{id}</c> body) — the same
    /// join as the list element <em>plus</em> the M6·b <see cref="ServerNetwork"/> block (a firewall probe
    /// cross-referenced against the instance's required ports) and the blueprint's cached RAWG
    /// <see cref="Server.Cover"/>/<see cref="Server.Hero"/> art. Returns <see langword="null"/> for an
    /// unknown id (the controller maps that to <c>404</c>). This is the place the detail view diverges
    /// from the list element — the list/stream deliberately omit <c>network</c>/<c>cover</c>/<c>hero</c>.
    /// <para>
    /// <paramref name="baseUrl"/> is the absolute origin the self-hosted cover/hero URLs are built from
    /// (<c>{scheme}://{host}</c> or the configured public base, passed by the controller). Pass
    /// <see langword="null"/>/blank to skip the art join entirely — what the off-request callers (the
    /// command-runner verify payload, the metrics-history existence check) do, so the <c>servers</c>
    /// stream patch stays byte-identical to the frozen M1·b shape.
    /// </para>
    /// </summary>
    public async Task<Server?> GetServerDetailAsync(string id, string? baseUrl, CancellationToken ct)
    {
        if (!_cache.EngineRead)
            return null;

        if (!_cache.Roster.TryGetValue(id, out Instance? instance))
            return null;

        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        Task observabilityTask = _observability.RefreshIfStaleAsync(ct);
        await Task.WhenAll(snapshotTask, observabilityTask).ConfigureAwait(false);

        Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshotTask.Result);
        Server server = BuildServer(id, instance, _cache.Statuses, _backups.Readings,
            metricsById, _options.HostId, _cache.IsStarting, _jobs.InFlightFor,
            IndexDiskBytes(snapshotTask.Result), OnlinePlayersOf(_players, _observability), _updateLag.Lookup,
            _runTimes.Lookup, BlueprintMinRamOf(_blueprints), _phases.ParkedLookup);

        // The required ports come from the instance roster we already read (Instance.Ports, no extra spawn);
        // the firewall probe is the only added I/O, bounded inside NetworkAggregator.
        ServerNetwork network = await _network
            .BuildServerNetworkAsync(id, instance.Ports, ct).ConfigureAwait(false);

        (string? cover, string? hero) = await ResolveArtAsync(server.Blueprint, baseUrl, ct).ConfigureAwait(false);
        return server with { Cover = cover, Hero = hero, Network = network };
    }

    /// <summary>
    /// Resolve the blueprint's cached cover (2:3 portrait) + hero (landscape banner) as absolute self-hosted
    /// URLs — the SAME <c>/library/{blueprint}/{slot}</c> endpoints the catalog serves, reusing
    /// <see cref="LibraryAggregator.ImageUrl"/> as the single URL-shape authority. A URL is built only when
    /// the cache row actually recorded a landed image file (else honest null — no source / unresolved).
    /// Degrades independently of everything else: no <paramref name="baseUrl"/>, a cache miss, or a read
    /// failure all leave both null without failing the detail (art is decorative, never load-bearing).
    /// </summary>
    private async Task<(string? Cover, string? Hero)> ResolveArtAsync(string blueprint, string? baseUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(blueprint))
            return (null, null);

        try
        {
            RawgEntry? row = await _rawg.GetAsync(blueprint, ct).ConfigureAwait(false);
            if (row is null)
                return (null, null);

            string? cover = string.IsNullOrWhiteSpace(row.CoverFile)
                ? null
                : LibraryAggregator.ImageUrl(baseUrl, blueprint, RawgCache.CoverSlot);
            string? hero = string.IsNullOrWhiteSpace(row.HeroFile)
                ? null
                : LibraryAggregator.ImageUrl(baseUrl, blueprint, RawgCache.HeroSlot);
            return (cover, hero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAWG cover/hero lookup for blueprint {Blueprint} failed; serving detail without art.", blueprint);
            return (null, null);
        }
    }

    private IReadOnlyList<Server> Join(
        IReadOnlyDictionary<string, Instance> roster,
        IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> statuses,
        IReadOnlyDictionary<string, BackupReading> backupReadings,
        Snap.Snapshot? snapshot)
    {
        Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshot);
        Dictionary<string, long> diskById = IndexDiskBytes(snapshot);

        var servers = new List<Server>(roster.Count);
        foreach ((string id, Instance instance) in roster)
            servers.Add(BuildServer(id, instance, statuses, backupReadings, metricsById,
                _options.HostId, _cache.IsStarting, _jobs.InFlightFor, diskById,
                OnlinePlayersOf(_players, _observability), _updateLag.Lookup, _runTimes.Lookup,
                BlueprintMinRamOf(_blueprints), _phases.ParkedLookup));

        // Deterministic order so polling/diffing is stable.
        servers.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return servers;
    }

    // Index per-instance metrics by id (the monitor guarantees unique ids per tick). internal so the
    // stream pumps index a snapshot by the same rule the REST reads use.
    internal static Dictionary<string, Snap.ServerMetrics> IndexMetrics(Snap.Snapshot? snapshot)
    {
        Dictionary<string, Snap.ServerMetrics> metricsById = new(StringComparer.Ordinal);
        if (snapshot is not null)
            foreach (Snap.ServerMetrics sm in snapshot.Servers)
                metricsById[sm.Id] = sm;
        return metricsById;
    }

    // Index the run-state-independent footprints by id. A separate array from the metrics rows because
    // it covers the monitor's whole watch-list — a stopped instance appears here and nowhere else. An
    // instance the walk couldn't read is absent (honest "not measured"), and an older monitor that
    // publishes no such array leaves every lookup empty rather than reporting zeros.
    internal static Dictionary<string, long> IndexDiskBytes(Snap.Snapshot? snapshot)
    {
        Dictionary<string, long> diskById = new(StringComparer.Ordinal);
        if (snapshot?.ServerDisks is { } disks)
            foreach (Snap.ServerDiskUsage d in disks)
                diskById[d.Id] = d.DiskBytes;
        return diskById;
    }

    // Build one Server (the shared list/detail element — detail adds the network block on top). status,
    // version and metrics are all independent + honest: a non-measured reading is "unknown", a missing
    // metrics row is null — never inferred from one another. Internal static so DomainPump can reuse it.
    // `activeJob` is JobRegistry.InFlightFor — the long-running operation that owns this instance right
    // now (an update, a backup), carried so a plain read tells a surface the instance is busy. It is kept
    // strictly beside status, never folded into it: status is run-state, this is what is being done to it.
    // `isStarting` is InstanceCache.IsStarting — the ONE place the starting-latch tri-state (see
    // InstanceCache's remarks) folds into the DTO's status; only consulted when the boolean reading
    // itself is already "up" (a stopped/crashed instance is never reported starting, even if the latch
    // somehow hadn't cleared — belt-and-suspenders alongside UpdateStatus's own latch-clear on stop/crash).
    // `onlinePlayers` answers "how many people are on this instance", and answers null for one whose
    // presence this host cannot see. Passing it as a function keeps this method free of the two services
    // behind that answer (the roster projection and the supervisor reading), exactly as `activeJob` does;
    // omitting it leaves the count honestly unknown rather than zero.
    // `isParked` is SupervisionPhaseIndex.IsParked — whether the watchdog is holding the instance out of
    // service. Like the starting latch, it is a real run-state the boolean reading cannot carry, and it is
    // only ever consulted when that reading is already "down".
    internal static Server BuildServer(
        string id,
        Instance instance,
        IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> statuses,
        IReadOnlyDictionary<string, BackupReading> backupReadings,
        IReadOnlyDictionary<string, Snap.ServerMetrics> metricsById,
        string hostId,
        Func<string, bool> isStarting,
        Func<string, Job?> activeJob,
        IReadOnlyDictionary<string, long>? diskBytesById = null,
        Func<string, int?>? onlinePlayers = null,
        Func<string, DateTimeOffset?>? updateAvailableSince = null,
        Func<string, Availability.RunTimes>? runTimes = null,
        Func<string, int?>? blueprintMinRamMb = null,
        Func<string, bool>? isParked = null)
    {
        string? libraryState = LibraryStateOf(instance);

        string status = ServerStatus.Unknown;
        string? version = null;
        bool? updateAvailable = null;
        string? latestVersion = null;
        DateTimeOffset? updateCheckedAt = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? stoppedAt = null;
        if (statuses.TryGetValue(id, out Reading<InstanceRuntimeStatus>? reading)
            && reading is { IsMeasured: true, Value: { } runtimeStatus })
        {
            // Three answers, not two. The engine reports `status: null` for an instance whose library
            // is not mounted — every reading a status takes comes out of a directory that is not there,
            // and an unreadable instance is not a stopped one. Nothing else in this block needs a guard:
            // the version, the update triple and the start time are read from the same absent directory
            // and arrive honestly null on their own.
            // A parked instance reads false here and is not stopped. The watchdog drains the process for
            // the span of a maintenance window while its desired state stays running and crash-restart
            // stays suppressed — so the reading is an honest "no process", and the phase beside it is what
            // says why. Only consulted on a down reading: a park the daemon has already released shows a
            // live process, and the process is the stronger fact.
            status = runtimeStatus.Status switch
            {
                true => isStarting(id) ? ServerStatus.Starting : ServerStatus.Running,
                false => isParked is not null && isParked(id) ? ServerStatus.Maintenance : ServerStatus.Stopped,
                null => ServerStatus.Unknown,
            };
            version = string.IsNullOrWhiteSpace(runtimeStatus.Version.Current)
                ? null
                : runtimeStatus.Version.Current;

            // All three update fields come off the same fast reading as the version itself. kgsm answers
            // them from the record it keeps beside each instance — written by the scheduler's sweep, which
            // owns the cadence and does the networked check — so this read touches no network and the API
            // runs no probe of its own. An instance nothing has checked yet reports the honest-null triple
            // (Checked=false), never a fabricated "no update".
            updateAvailable = runtimeStatus.Version.UpdatesAvailable;
            latestVersion = runtimeStatus.Version.Latest;
            updateCheckedAt = runtimeStatus.Version.CheckedAt;

            // Process start time → an honest start timestamp (the SPA derives uptime from it). Only a
            // UTC-kind value is defensible: an Unspecified/Local kind carries an unknown offset → null,
            // never a guessed zone. This covers CONTAINERS, whose start time Docker supplies; a native
            // instance is dated below by the watchdog, which is the thing that spawned it.
            DateTime? start = runtimeStatus.Process.StartTime;
            startedAt = start is { Kind: DateTimeKind.Utc } utc ? new DateTimeOffset(utc) : null;
        }

        // The watchdog's run clock wins for a NATIVE instance: kgsm dates a run from a local pid file, and
        // one the watchdog spawned has none, so the engine reading above is null for exactly the instances
        // this host supervises. It is the run-state authority for those, so it is also what dates them.
        // A container keeps the engine's reading (the daemon does not supervise one) and has no stop time
        // to report. Both stay honestly null when nothing dates them.
        //
        // An instance with no reported runtime takes neither branch. Which supervisor to ask is exactly
        // what an unreadable instance does not say, and the ledger can still hold a row from before its
        // disk went — dating a run off it would put a start time on a server nobody can see.
        if (runTimes is not null)
        {
            Availability.RunTimes rt = runTimes(id);
            if (instance.Runtime == InstanceRuntime.Native)
            {
                startedAt = rt.SpawnedAt ?? startedAt;
                // Only meaningful while the instance is NOT running: the ledger keeps the last run's end,
                // and reporting it beside a live run would date a stop that has been superseded.
                stoppedAt = status == ServerStatus.Stopped ? rt.LastExitedAt : null;
            }
        }

        // Metrics only when the monitor produced a row for this id; otherwise honest null. The shared
        // MetricsMapping is what keeps this byte-identical to the M2 servers/{id}/metrics tick.
        ServerMetricsDto? metrics = metricsById.TryGetValue(id, out Snap.ServerMetrics? m)
            ? MetricsMapping.ToServerMetrics(m)
            : null;

        // The footprint is independent of run-state for the same reason backups are: a stopped instance
        // still occupies its disk. It comes from the snapshot's own watch-list-wide array, never from the
        // metrics row, so it is present exactly when the monitor measured it.
        long? diskBytes = diskBytesById is not null && diskBytesById.TryGetValue(id, out long bytes)
            ? bytes
            : null;

        // Backup standing is independent of run-state and of the metrics row — an instance that is stopped,
        // or that the monitor has no sample for, still has whatever backups it has. A missing entry is the
        // honest "not scanned yet" (both fields null), never a claim that there are none.
        BackupReading backups = backupReadings.TryGetValue(id, out BackupReading? br)
            ? br
            : BackupReading.Unknown;

        // What a start is expected to cost the node, read in the SAME order KGSM's memory gate reads it
        // so the panel's warning and the engine's refusal cannot disagree about which figure applies.
        // The cap first — the cgroup ceiling the watchdog enforces, so it bounds what the node can lose
        // and an operator chose it; the blueprint's advisory figure only when there is no cap.
        //
        // Published as the REQUIREMENT, never as a verdict: whether there is room depends on what the
        // node has free at the instant the engine looks, and this record would be stale about that the
        // moment it serialized. Neither declared leaves both null — the gate cannot answer either, and a
        // substituted default would put an invented requirement in front of a real start.
        int? startMemoryMb = null;
        string? startMemorySource = null;
        if (instance.MemoryCapMb is { } capMb and > 0)
        {
            startMemoryMb = capMb;
            startMemorySource = StartMemorySource.Cap;
        }
        else if (blueprintMinRamMb?.Invoke(instance.Blueprint) is { } minRamMb and > 0)
        {
            startMemoryMb = minRamMb;
            startMemorySource = StartMemorySource.Blueprint;
        }

        return new Server(
            Id: id,
            // The label, not the id. kgsm-lib resolves an instance with no label of its own to its id
            // (including the offline-library case, where the config stating it cannot be read), so there
            // is nothing to coalesce here and nothing that could render as a nameless row.
            Name: instance.DisplayName,
            Blueprint: instance.Blueprint,
            Status: status,
            Version: version,
            // Null when the engine reported none — it omits `runtime` entirely for an instance whose
            // library is not mounted, because the config naming it is on the disk that is gone. kgsm-lib
            // carries that as a null on its nullable enum, and the discard arm below is what keeps it one:
            // a supervision type nobody read has no name here. This IS a divergence from the frozen
            // native|container pair, taken on the rule that outranks it.
            Runtime: instance.Runtime switch
            {
                InstanceRuntime.Container => "container",
                InstanceRuntime.Native => "native",
                _ => null,
            },
            HostId: hostId,
            SteamAppId: string.IsNullOrWhiteSpace(instance.SteamAppId) ? "0" : instance.SteamAppId,
            ClientSteamAppId: string.IsNullOrWhiteSpace(instance.ClientSteamAppId) ? "0" : instance.ClientSteamAppId,
            IsSteamAccountRequired: instance.IsSteamAccountRequired,
            Metrics: metrics,
            UpdateAvailable: updateAvailable,
            LatestVersion: latestVersion,
            UpdateCheckedAt: updateCheckedAt,
            // Only dated while the gap is actually open. The index is refreshed on its own cadence, so
            // pairing it with this reading is what stops a just-updated instance from carrying the age of
            // the gap it just closed.
            UpdateAvailableSince: updateAvailable == true ? updateAvailableSince?.Invoke(id) : null,
            StartedAt: startedAt,
            StoppedAt: stoppedAt,
            ConnectPort: ConnectPortOf(instance.Ports),
            Note: NoteOf(instance),
            LastBackup: backups.Latest,
            BackupCount: backups.Count,
            ActiveJob: activeJob(id),
            DiskBytes: diskBytes,
            OnlinePlayers: onlinePlayers?.Invoke(id),
            StartMemoryMb: startMemoryMb,
            StartMemorySource: startMemorySource,
            // Both straight off the instance's own config — the engine resolves the name from the path
            // per invocation, and answers "unregistered" for a root nothing declares. Blank on an engine
            // that predates libraries, which stays blank rather than becoming a guessed root.
            Library: string.IsNullOrWhiteSpace(instance.Library) ? null : instance.Library,
            LibraryPath: string.IsNullOrWhiteSpace(instance.LibraryDir) ? null : instance.LibraryDir,
            LibraryState: libraryState);
    }

    /// <summary>
    /// Whether this instance's library root is reachable — the second axis beside run-state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine's own measurement, made per invocation from the instance registry — the one read that
    /// works when the instance's own config cannot be opened, which is precisely the case this reports.
    /// It is the same answer a lifecycle verb's refusal will give, rather than a second opinion about it.
    /// </para>
    /// <para>
    /// Absent is <see langword="null"/>, never <c>offline</c>. Only an engine predating libraries
    /// leaves it unstated, and reading that as a disk being gone would put every server on such a host
    /// behind a warning about hardware that is fine.
    /// </para>
    /// </remarks>
    private static string? LibraryStateOf(Instance instance) => instance.LibraryState switch
    {
        InstanceLibraryState.Online => "online",
        InstanceLibraryState.Offline => "offline",
        // The engine's own word for an instance whose root no library claims. A measurement: the files
        // are there and readable, and this host simply holds no entry naming the root they are under.
        InstanceLibraryState.Unregistered => "unregistered",
        _ => null,
    };

    // The operator-authored note, or null when the instance has no note. kgsm-lib decodes the body
    // (Instance.NoteBody); attribution is honest-null when the config carries none — a hand-edited
    // note renders without a fabricated author, and an unparseable timestamp is dropped rather than
    // guessed. internal so DomainPump's change-detection and the note controller share one rule.
    internal static ServerNote? NoteOf(Instance instance)
    {
        string? body = instance.NoteBody;
        if (string.IsNullOrEmpty(body))
            return null;

        string? by = string.IsNullOrWhiteSpace(instance.NoteUpdatedBy) ? null : instance.NoteUpdatedBy;
        DateTimeOffset? at = DateTimeOffset.TryParse(instance.NoteUpdatedAt,
            CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;

        return new ServerNote(body, by, at);
    }

    // The player-facing connect port: the FIRST port this instance requires. kgsm writes the game/connect
    // port first in the blueprint's port spec, and NetworkAggregator's expansion preserves that source
    // order, so this is the same port the detail view's network.required[0] carries — one rule, two
    // surfaces that agree. Inverted ranges are skipped defensively (as they are there); no ports, or only
    // malformed ones, is honest null rather than a fabricated default.
    private static int? ConnectPortOf(IReadOnlyList<PortMapping>? ports)
    {
        if (ports is null) return null;
        foreach (PortMapping m in ports)
            if (m is not null && m.End >= m.Start && m.Start > 0)
                return m.Start;
        return null;
    }

}

/// <summary>
/// The result of reading this host's server list: whether the engine was actually read
/// (<see cref="EngineRead"/> == false ⇒ a transient read failure, not "zero servers") and the servers
/// from that read. Consumed by <c>GET /servers</c> (503 on a failed read) and the <c>DomainPump</c>
/// (skip the tick on a failed read) so neither mistakes an unread roster for an empty one.
/// </summary>
public readonly record struct ServersRead(bool EngineRead, IReadOnlyList<Server> Servers);
