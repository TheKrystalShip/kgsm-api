using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
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
    private readonly ILogger<ServerAggregator> _logger;

    public ServerAggregator(
        ApiOptions options,
        MonitorClient monitor,
        NetworkAggregator network,
        RawgStore rawg,
        InstanceCache cache,
        ILogger<ServerAggregator> logger)
    {
        _options = options;
        _monitor = monitor;
        _network = network;
        _rawg = rawg;
        _cache = cache;
        _logger = logger;
    }

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

        // Monitor scrape (a socket read, not a process spawn) runs concurrently with the sync cache read.
        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        await snapshotTask.ConfigureAwait(false);

        IReadOnlyList<Server> servers = Join(_cache.Roster, _cache.Statuses, snapshotTask.Result);
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
        await snapshotTask.ConfigureAwait(false);

        Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshotTask.Result);
        Server server = BuildServer(id, instance, _cache.Statuses, metricsById, _options.HostId, _cache.IsStarting);

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
        Snap.Snapshot? snapshot)
    {
        Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshot);

        var servers = new List<Server>(roster.Count);
        foreach ((string id, Instance instance) in roster)
            servers.Add(BuildServer(id, instance, statuses, metricsById, _options.HostId, _cache.IsStarting));

        // Deterministic order so polling/diffing is stable.
        servers.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return servers;
    }

    // Index per-instance metrics by id (the monitor guarantees unique ids per tick).
    private static Dictionary<string, Snap.ServerMetrics> IndexMetrics(Snap.Snapshot? snapshot)
    {
        Dictionary<string, Snap.ServerMetrics> metricsById = new(StringComparer.Ordinal);
        if (snapshot is not null)
            foreach (Snap.ServerMetrics sm in snapshot.Servers)
                metricsById[sm.Id] = sm;
        return metricsById;
    }

    // Build one Server (the shared list/detail element — detail adds the network block on top). status,
    // version and metrics are all independent + honest: a non-measured reading is "unknown", a missing
    // metrics row is null — never inferred from one another. Internal static so DomainPump can reuse it.
    // `isStarting` is InstanceCache.IsStarting — the ONE place the starting-latch tri-state (see
    // InstanceCache's remarks) folds into the DTO's status; only consulted when the boolean reading
    // itself is already "up" (a stopped/crashed instance is never reported starting, even if the latch
    // somehow hadn't cleared — belt-and-suspenders alongside UpdateStatus's own latch-clear on stop/crash).
    internal static Server BuildServer(
        string id,
        Instance instance,
        IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> statuses,
        IReadOnlyDictionary<string, Snap.ServerMetrics> metricsById,
        string hostId,
        Func<string, bool> isStarting)
    {
        string status = ServerStatus.Unknown;
        string? version = null;
        bool? updateAvailable = null;
        DateTimeOffset? startedAt = null;
        if (statuses.TryGetValue(id, out Reading<InstanceRuntimeStatus>? reading)
            && reading is { IsMeasured: true, Value: { } runtimeStatus })
        {
            status = runtimeStatus.Status
                ? (isStarting(id) ? ServerStatus.Starting : ServerStatus.Running)
                : ServerStatus.Stopped;
            version = string.IsNullOrWhiteSpace(runtimeStatus.Version.Current)
                ? null
                : runtimeStatus.Version.Current;

            // Honest update flag: kgsm-lib reports UpdatesAvailable null unless it actually ran the
            // (networked) check (Version.Checked). The roster status is read fast (no per-poll network
            // probe), so this is null in practice today — never a fabricated "false" for an unchecked
            // instance. See Server.UpdateAvailable for the cost rationale.
            updateAvailable = runtimeStatus.Version.UpdatesAvailable;

            // Process start time → an honest start timestamp (the SPA derives uptime from it). Only a
            // UTC-kind value is defensible: kgsm emits start_time as a non-ISO local string the lib can't
            // parse, so the only non-null that reaches here is a parseable ISO-UTC one. An Unspecified/Local
            // kind would be an unknown offset → null, never a guessed zone. See Server.StartedAt.
            DateTime? start = runtimeStatus.Process.StartTime;
            startedAt = start is { Kind: DateTimeKind.Utc } utc ? new DateTimeOffset(utc) : null;
        }

        // Metrics only when the monitor produced a row for this id; otherwise honest null. The shared
        // MetricsMapping is what keeps this byte-identical to the M2 servers/{id}/metrics tick.
        ServerMetricsDto? metrics = metricsById.TryGetValue(id, out Snap.ServerMetrics? m)
            ? MetricsMapping.ToServerMetrics(m)
            : null;

        return new Server(
            Id: id,
            Name: string.IsNullOrWhiteSpace(instance.Name) ? id : instance.Name,
            Blueprint: CleanBlueprintId(instance),
            Status: status,
            Version: version,
            Runtime: instance.Runtime == InstanceRuntime.Container ? "container" : "native",
            HostId: hostId,
            SteamAppId: string.IsNullOrWhiteSpace(instance.SteamAppId) ? "0" : instance.SteamAppId,
            ClientSteamAppId: string.IsNullOrWhiteSpace(instance.ClientSteamAppId) ? "0" : instance.ClientSteamAppId,
            IsSteamAccountRequired: instance.IsSteamAccountRequired,
            Metrics: metrics,
            UpdateAvailable: updateAvailable,
            StartedAt: startedAt);
    }

    // The clean blueprint id, e.g. "factorio" from ".../factorio.bp.yaml". Unified blueprints are
    // "<name>.bp.yaml", so strip that compound suffix deliberately — Path.GetFileNameWithoutExtension
    // (what Instance.Blueprint uses) only drops the last extension and would leave "factorio.bp".
    // internal so the M6·b NetworkAggregator can reuse it for the host open-ports `app` join.
    internal static string CleanBlueprintId(Instance instance)
    {
        string file = Path.GetFileName(instance.BlueprintFile);
        foreach (string suffix in BlueprintSuffixes)
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return file[..^suffix.Length];

        // Fallback for an unexpected shape: the lib's own derivation (drops the last extension).
        return string.IsNullOrEmpty(instance.Blueprint) ? file : instance.Blueprint;
    }

    private static readonly string[] BlueprintSuffixes = [".bp.yaml", ".bp.yml"];
}

/// <summary>
/// The result of reading this host's server list: whether the engine was actually read
/// (<see cref="EngineRead"/> == false ⇒ a transient read failure, not "zero servers") and the servers
/// from that read. Consumed by <c>GET /servers</c> (503 on a failed read) and the <c>DomainPump</c>
/// (skip the tick on a failed read) so neither mistakes an unread roster for an empty one.
/// </summary>
public readonly record struct ServersRead(bool EngineRead, IReadOnlyList<Server> Servers);
