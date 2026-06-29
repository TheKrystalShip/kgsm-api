using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using Snap = TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.Api.Services.Aggregation;

/// <summary>
/// Builds this host's <see cref="Server"/> list (architecture §3) for the M1·b read surface — the
/// project's central join: the kgsm engine's domain + run-state (via kgsm-lib) ⋈ the per-instance
/// metrics (via kgsm-monitor), keyed on the instance id. The roster is authoritative from kgsm-lib
/// (<see cref="IInstanceService.GetAll"/>); run-state/version come from
/// <see cref="IInstanceService.GetAllStatuses"/>; metrics are looked up by id in the monitor
/// snapshot. A server with no metrics row (monitor absent, or simply not running) gets
/// <c>metrics: null</c> — never a fabricated zero (the honesty invariant).
/// </summary>
/// <remarks>
/// The two kgsm-lib calls are synchronous process spawns (they shell <c>kgsm.sh</c>), so they run
/// on the thread pool concurrently with the async monitor scrape and never block the request thread.
/// <see cref="IInstanceService"/> is a <em>transient</em> kgsm-lib service, so it is resolved
/// per-call from the provider (the same optional-resolve pattern <c>HostAggregator</c> uses for the
/// watchdog client) rather than captured in this singleton. The engine is base, not a leaf: if it is
/// unconfigured/unregistered the list is empty and a misconfiguration is logged once — there is no
/// §4·b "engine" capability to render absent.
/// </remarks>
public sealed class ServerAggregator
{
    private readonly ApiOptions _options;
    private readonly MonitorClient _monitor;
    private readonly NetworkAggregator _network;
    private readonly RawgStore _rawg;
    private readonly IServiceProvider _services;
    private readonly ILogger<ServerAggregator> _logger;

    // Latch so a persistent engine misconfiguration is logged once, not on every poll.
    private int _engineUnavailableLogged;

    public ServerAggregator(
        ApiOptions options,
        MonitorClient monitor,
        NetworkAggregator network,
        RawgStore rawg,
        IServiceProvider services,
        ILogger<ServerAggregator> logger)
    {
        _options = options;
        _monitor = monitor;
        _network = network;
        _rawg = rawg;
        _services = services;
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
        // Domain read (blocking process spawns) and the metrics scrape run concurrently.
        Task<DomainReadout> domainTask = Task.Run(ReadDomain, ct);
        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        await Task.WhenAll(domainTask, snapshotTask).ConfigureAwait(false);

        DomainReadout domain = domainTask.Result;
        return domain.EngineRead
            ? new ServersRead(true, Join(domain, snapshotTask.Result))
            : new ServersRead(false, []);
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
        Task<DomainReadout> domainTask = Task.Run(ReadDomain, ct);
        Task<Snap.Snapshot?> snapshotTask = _monitor.GetLatestAsync(ct);
        await Task.WhenAll(domainTask, snapshotTask).ConfigureAwait(false);

        DomainReadout domain = domainTask.Result;
        if (!domain.Instances.TryGetValue(id, out Instance? instance))
            return null;

        Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshotTask.Result);
        Server server = BuildServer(id, instance, domain.Statuses, metricsById);

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

    /// <summary>The kgsm-lib domain read: the instance roster + each instance's status reading.</summary>
    private DomainReadout ReadDomain()
    {
        // IInstanceService is transient and only registered when the engine is provisioned; resolve
        // optionally so an unconfigured engine degrades to an empty list rather than throwing.
        var instances = _services.GetService(typeof(IInstanceService)) as IInstanceService;
        if (instances is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — /servers will be empty.");
            // A persistent misconfiguration, not a transient read failure: report an honest empty roster
            // (EngineRead=true) so the surface shows "no servers" rather than perpetually keeping stale.
            return DomainReadout.Empty;
        }

        try
        {
            // GetAllOrNull distinguishes a FAILED read (null) from a genuine empty roster (kgsm emits "{}",
            // which deserializes to a non-null empty dict). A failed read must NOT be served as "zero
            // servers" — that's what wiped the SPA's list (a 200 [] replacing it) and made the DomainPump
            // tombstone every instance. Report it as unread so the caller keeps last-known state / 503s.
            Dictionary<string, Instance>? roster = instances.GetAllOrNull();
            if (roster is null)
            {
                _logger.LogWarning(
                    "kgsm instance-roster read failed (the list command errored or returned unparseable "
                    + "output) — preserving last-known state, NOT reporting zero servers.");
                return DomainReadout.Unread;
            }

            // fast: skip the per-instance network update-check (~20x faster). Version.Current is still
            // reported; only Latest/UpdatesAvailable go null — which this DTO does not emit anyway.
            Dictionary<string, Reading<InstanceRuntimeStatus>> statuses = instances.GetAllStatuses(fast: true);

            if (roster.Count == 0)
                _logger.LogDebug("kgsm reported an empty instance roster (genuinely zero instances).");

            return new DomainReadout(roster, statuses, EngineRead: true);
        }
        catch (Exception ex)
        {
            // A read that threw is a FAILED read, not "zero servers" — surface it as unread (the caller
            // keeps last-known state) and log so an operator can see the engine read broke.
            _logger.LogWarning(ex, "kgsm domain read threw; preserving last-known state, not reporting zero servers.");
            return DomainReadout.Unread;
        }
    }

    private IReadOnlyList<Server> Join(DomainReadout domain, Snap.Snapshot? snapshot)
    {
        Dictionary<string, Snap.ServerMetrics> metricsById = IndexMetrics(snapshot);

        var servers = new List<Server>(domain.Instances.Count);
        foreach ((string id, Instance instance) in domain.Instances)
            servers.Add(BuildServer(id, instance, domain.Statuses, metricsById));

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
    // metrics row is null — never inferred from one another.
    private Server BuildServer(
        string id,
        Instance instance,
        IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> statuses,
        IReadOnlyDictionary<string, Snap.ServerMetrics> metricsById)
    {
        string status = ServerStatus.Unknown;
        string? version = null;
        bool? updateAvailable = null;
        DateTimeOffset? startedAt = null;
        if (statuses.TryGetValue(id, out Reading<InstanceRuntimeStatus>? reading)
            && reading is { IsMeasured: true, Value: { } runtimeStatus })
        {
            status = runtimeStatus.Status ? ServerStatus.Running : ServerStatus.Stopped;
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
            HostId: _options.HostId,
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

    /// <summary>
    /// The kgsm-lib domain read result: the instance roster, the per-instance status readings, and
    /// whether the engine was actually read. <see cref="EngineRead"/> is the honesty axis — false means
    /// the roster read FAILED (transient), true means it succeeded (the roster may legitimately be empty).
    /// </summary>
    private sealed record DomainReadout(
        IReadOnlyDictionary<string, Instance> Instances,
        IReadOnlyDictionary<string, Reading<InstanceRuntimeStatus>> Statuses,
        bool EngineRead)
    {
        /// <summary>A successful read of an empty roster (or an unconfigured engine): honestly zero servers.</summary>
        public static readonly DomainReadout Empty = new(
            new Dictionary<string, Instance>(),
            new Dictionary<string, Reading<InstanceRuntimeStatus>>(),
            EngineRead: true);

        /// <summary>A FAILED read: the engine could not be read, so the caller must keep last-known state.</summary>
        public static readonly DomainReadout Unread = new(
            new Dictionary<string, Instance>(),
            new Dictionary<string, Reading<InstanceRuntimeStatus>>(),
            EngineRead: false);
    }
}

/// <summary>
/// The result of reading this host's server list: whether the engine was actually read
/// (<see cref="EngineRead"/> == false ⇒ a transient read failure, not "zero servers") and the servers
/// from that read. Consumed by <c>GET /servers</c> (503 on a failed read) and the <c>DomainPump</c>
/// (skip the tick on a failed read) so neither mistakes an unread roster for an empty one.
/// </summary>
public readonly record struct ServersRead(bool EngineRead, IReadOnlyList<Server> Servers);
