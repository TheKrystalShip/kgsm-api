using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// A game server (a kgsm instance on this host) — the <strong>honest realization</strong> of the
/// <c>architecture.html §3</c> <c>Server</c> example, frozen at M1·b. It is the join of the kgsm
/// engine's domain + run-state (via kgsm-lib) with the per-instance metrics (via kgsm-monitor),
/// keyed on the instance id.
/// <para>
/// The aspirational v0.3 example asks for fields that have <strong>no honest backing source
/// today</strong> — emitting them verbatim would be fabrication (the sin that scrapped the old
/// kgsm-api). So this DTO deliberately diverges, and that divergence is the contract:
/// </para>
/// <list type="bullet">
///   <item><description><c>status</c> is the honest, closed
///     <c>running|starting|maintenance|stopped|unknown</c> vocabulary — never the aspirational
///     <c>online|offline|updating|crashed|installing</c>. <c>running</c>/<c>stopped</c>/<c>unknown</c>
///     derive from kgsm-lib's <c>Reading&lt;InstanceRuntimeStatus&gt;</c>.
///     <c>starting</c> is a REAL run-state, not a job state: the window between a <c>server.started</c>
///     event (the process spawned) and the matching <c>server.ready</c> event (the watchdog's log-scrape
///     confirms the game finished booting) — the boolean status reading alone can't tell these apart (the
///     process is genuinely "up" for both), so <see cref="Services.Aggregation.InstanceCache"/> tracks the
///     window explicitly (a "starting latch") and <c>ServerAggregator.BuildServer</c> is the one place that
///     folds it into <c>status</c>. See <c>InstanceCache</c>'s remarks for why the periodic boolean
///     reconcile must never be allowed to promote <c>starting</c> back to <c>running</c> on its own.
///     <c>maintenance</c> is the other real run-state the boolean cannot carry: the watchdog holds a parked
///     instance stopped with its intent still set to running, so the reading is false while nothing is
///     wrong and nobody asked for it to be down (see <c>Services.Availability.SupervisionPhaseIndex</c>).</description></item>
///   <item><description><c>metrics</c> preserves the monitor's native units: <c>cpuPctCore</c>
///     (% of <em>one</em> core, can exceed 100 — NOT the host's 0–100), <c>memBytes</c>, nullable
///     <c>io*</c>. The whole block is <c>null</c> when no per-server sample is available.</description></item>
///   <item><description>Omitted as unsourceable: <c>cpu</c> 0–100,
///     <c>ram.max</c> (no memory limit), <c>ip</c> (not resolved), <c>updatedAt</c> (no state-change
///     tracking until the M2 stream), and the curated <c>game</c> display name (we emit the real
///     <c>blueprint</c> id instead — blueprint metadata curation is deferred, never guessed).</description></item>
///   <item><description><c>onlinePlayers</c> is a count, not a roster, and is <c>null</c> for a server
///     whose presence this host cannot observe — never a fabricated <c>0</c>. <c>players.max</c> stays
///     absent: no instance declares a capacity, so there is nothing honest to divide by.</description></item>
///   <item><description><c>startedAt</c> is <strong>wired but honestly null in practice</strong> (see its
///     per-field note): the referenced kgsm-lib cannot parse kgsm's non-ISO <c>start_time</c>. It is
///     present-as-null so the SPA binds a stable shape, and is never fabricated.</description></item>
/// </list>
/// Keys are always present with explicit <c>null</c> values (honest unknown over omission), so the
/// SPA binds a stable shape.
/// </summary>
public sealed record Server(
    // Stable kgsm instance id and the join key (== monitor ServerMetrics.Id == the lib dict key). It is
    // auto-generated at install, path-safe, and immutable for the instance's lifetime — every route,
    // keyed store, audit row and metrics series on this host is keyed on it, and nothing renames it.
    string Id,
    // The label a person reads this server by (the instance's `display_name`), which an operator changes
    // whenever they like through PUT /servers/{id}/display-name. Free text — spaces, punctuation and emoji
    // are all legal, because it never reaches a path — and NOT unique: two servers may carry the same
    // label, so a surface that needs to tell them apart shows Id beside it. An instance with no label of
    // its own reads as its Id, so this is never blank and never a fabricated prettier name.
    string Name,
    // Blueprint id this instance was installed from (the honest analog of the aspirational `game`).
    string Blueprint,
    // running | starting | maintenance | stopped | unknown — see ServerStatus. running/stopped/unknown come
    // from Reading<InstanceRuntimeStatus>; starting is the InstanceCache starting-latch window and
    // maintenance is the watchdog's park, both layered on top by ServerAggregator.BuildServer (see the
    // class remarks above).
    string Status,
    // Installed version (InstanceRuntimeStatus.Version.Current). Null when the status is unknown
    // or kgsm reports no version. This is what IS installed; whether something newer exists is the
    // separate UpdateAvailable/LatestVersion pair below.
    string? Version,
    // native | container — the supervision discriminator (Instance.Runtime), lower-cased.
    //
    // NULL when the instance's library is not mounted. The engine omits the field entirely from an
    // offline instance's payload (its config is on the disk that is gone), and kgsm-lib models that as a
    // nullable enum, so "not reported" arrives as null and travels here as null. A honest unknown is the
    // divergence from the frozen native|container pair, on the same rule as every other field here:
    // never a plausible default over an unmeasured value — reading an unreported runtime as native would
    // send a surface to the watchdog for a container's run-state and get a confident wrong answer back.
    string? Runtime,
    // The host this server runs on (architecture §4·a). Always this api's single host.
    string HostId,
    // Dedicated-server Steam App ID ("0" for non-Steam games). Static per-blueprint.
    string SteamAppId,
    // Client Steam App ID for launch/connect deeplinks ("0" for non-Steam games). Static per-blueprint.
    string ClientSteamAppId,
    // Whether a Steam account is required to download the server. Static per-blueprint.
    bool IsSteamAccountRequired,
    // Per-instance resource usage from the monitor, or null when the monitor is absent/unreachable
    // or has no sample for this instance (e.g. a stopped server has no cgroup/process tree). Null
    // here is the honest "not measurable now" — never a fabricated zero.
    ServerMetricsDto? Metrics,
    // Whether a newer version is available (VersionInfo.UpdatesAvailable). kgsm answers this from the
    // record it keeps beside each instance, so reading it costs no network here — the networked check is
    // the scheduler's, on its own cadence. Null for an instance nothing has checked yet, and for one whose
    // last check could not reach upstream: never a fabricated false ("no update") in either case. The
    // backend 409s an update-on-running synchronously, so the SPA pairs this with status to gate its
    // Update chip.
    bool? UpdateAvailable = null,
    // The target version an update would land at (VersionInfo.Latest) when UpdateAvailable is true, and null
    // otherwise (unchecked). Sourced from the same reading as UpdateAvailable; honest-null when nothing has
    // checked this instance. The SPA renders this beside the Update chip
    // (the "→ <version>" affordance) — a truthy string here is what "lights up" the Update surface.
    string? LatestVersion = null,
    // When this instance's upstream was last really fetched (UTC, VersionInfo.CheckedAt). Honest-null until
    // a check has succeeded at least once — never a fabricated timestamp, and never stamped onto a value
    // read back off the engine's record. Lets the SPA surface "checked N min ago" so freshness is visible.
    DateTimeOffset? UpdateCheckedAt = null,
    // When this instance was FIRST seen to be behind — the engine's oldest still-standing
    // server.update.available notice, read from the journal (Services/Availability/UpdateLagIndex).
    // Distinct from UpdateCheckedAt, which says when the last check ran: this says how long the gap has
    // been open, which is what makes an update overdue rather than merely pending. Null whenever there is
    // nothing to date it by — no update outstanding, an engine that emits no such event, or a notice
    // older than the index's lookback. Never fabricated from the check time, and never shown on its own:
    // a surface pairs it with UpdateAvailable, so an age can't outlive the gap it measures.
    DateTimeOffset? UpdateAvailableSince = null,
    // When the running process started, or null when stopped/unknown. Two sources, by runtime: a native
    // instance is dated by the WATCHDOG's spawn time, because kgsm reads a start time from a local pid file
    // and a watchdog-spawned native has none — the process lives in a cgroup the daemon owns. A container is
    // dated by the engine's own process.start_time, which Docker supplies and kgsm emits as ISO-UTC.
    // Never a guessed timezone: a reading without a definite UTC kind is dropped rather than assumed local.
    DateTimeOffset? StartedAt = null,
    // When the LAST run ended, or null when nothing dates one. Read from the watchdog's durable run
    // ledger for a native instance — the run's own last output, not the moment the supervisor noticed the
    // cgroup had emptied. Null for a container (Docker's finish time is not carried through the engine)
    // and for an instance with no recorded runs; a surface renders the honest unknown rather than
    // implying the server has never run.
    DateTimeOffset? StoppedAt = null,
    // The player-facing connect PORT — the first of this instance's required ports (kgsm lists the
    // game/connect port first in the blueprint), read straight off the cached Instance.Ports. Unlike the
    // Network block below it is present on the list, the `servers` stream AND the detail view: this is pure
    // domain truth already in the roster, with no firewall probe behind it, so carrying it everywhere costs
    // nothing — and it is what lets a surface render and copy `host:port` from a list row alone, without
    // fetching the detail. Null when the instance declares no ports (honest unknown, never a fabricated 0
    // and never a guessed game default).
    int? ConnectPort = null,
    // RAWG.io cover-art + hero banner for this server's blueprint, joined from this host's cached library
    // metadata (RawgStore, keyed on the blueprint id == this server's Blueprint) — populated ONLY on the
    // GET /servers/{id} detail view, like Network; omitted on the list + the `servers` stream so those stay
    // byte-identical to the frozen M1·b shape. Absolute, directly-renderable URLs to this api's own
    // self-hosted /library/{blueprint}/{cover|hero} image endpoints (the SAME bytes the catalog serves), or
    // null when no image is cached / no source. Cover is the 2:3 portrait capsule; Hero is the landscape
    // background_image_additional banner the detail page renders behind the title. Never a CDN hotlink,
    // never fabricated.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cover = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hero = null,
    // The firewall/ports cross-reference (M6·b) — populated ONLY on the GET /servers/{id} detail view
    // (and the servers/{id}/network WS patch); omitted entirely on the list + the `servers` stream, so
    // those stay byte-identical to the frozen M1·b shape (detail ≠ list, the first such split). See
    // ServerNetwork for the honest-unknown + reserved-`reachable` semantics.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ServerNetwork? Network = null,
    // The operator-authored server note (mods, rules, a heads-up before joining), read off the cached
    // Instance. Unlike Cover/Hero/Network this rides the list, the detail AND the `servers` stream: the
    // dashboard tile renders it, so a list row must carry it without a detail fetch, and an edit in one
    // browser must reach another without a refresh. Null when the instance has no note — the honest
    // "nothing written", which is what makes the SPA hide the card for a non-operator.
    ServerNote? Note = null,
    // This instance's most recent backup, as its own manifest records it — the same ServerBackup shape the
    // GET /servers/{id}/backups listing serves, through the same mapper, so a backup described on a server
    // card and in the list cannot disagree. Carried in full rather than as a bare timestamp because the
    // facts a surface needs to be honest about a snapshot (what it captured, how big it is, which version,
    // whether it is an archive) all live on the manifest; a surface renders the fields the manifest
    // actually carried and omits the rest. Sourced from BackupCache, and like Note it rides the list, the
    // detail AND the `servers` stream, because the dashboard summarizes backup freshness across the whole
    // roster and must not need a detail fetch per server to do it.
    //
    // Null means EITHER "no backups" OR "not scanned yet" — BackupCount is what separates them (0 vs null).
    // Never a fabricated timestamp for an instance the scan has not reached.
    ServerBackup? LastBackup = null,
    // How many backups this instance holds. 0 is a MEASURED zero (the engine was read and reported none);
    // null is "not scanned yet" (cold cache, or the engine read failed and no prior reading exists). Keeping
    // those apart is what lets a surface say "no backups yet" only when that is actually known, and show an
    // honest unknown otherwise.
    int? BackupCount = null,
    // The long-running operation that currently owns this instance (an update downloading and deploying, a
    // backup being taken), or null when nothing is in flight. This is the `jobs` topic's own record — the
    // one JobRegistry holds — carried on the server so a surface learns "this instance is busy" from a plain
    // read instead of only from the transition frame: a browser that reloads mid-update, or one that
    // connects after the job started, sees it. It rides the list, the detail AND the `servers` stream for
    // that reason, and DomainPump diffs it, so start and finish reach every open panel within a tick.
    //
    // Two sources, both measured, never fabricated: a job this API issued, and — via
    // server.update.started/finished — an update kgsm is running for some OTHER entrypoint (the CLI, the
    // assistant, the bot). They share one slot, so this is at most one record whoever started it.
    //
    // It is NOT a status. Status stays the run-state vocabulary above; a surface that wants to render
    // "updating" joins the two itself, exactly as it joins status with metrics.
    Job? ActiveJob = null,
    // This instance's on-disk footprint in bytes (the monitor's slow working-dir walk), or null when it
    // has not been measured. It sits OUTSIDE Metrics deliberately: everything in that block is a runtime
    // reading that exists only while the server runs, whereas the space an installed instance occupies is
    // a property of its files — the monitor publishes it for its whole watch-list (Snapshot.ServerDisks),
    // so a stopped instance carries an honest figure here while its Metrics block is null. That is what
    // lets a card show disk for every server and cpu/mem/net only for the running ones.
    //
    // Rides the list, the detail AND the `servers` stream so a card needs no detail fetch. DomainPump
    // does NOT diff it (see CoreChanged) — it is a metric, and the roster metric frame is what keeps it
    // live; carrying it here is the hydrate, not the feed.
    long? DiskBytes = null,
    // How many people are connected to this instance right now, or null when this host cannot see who is
    // on it (the game declares no join/leave detection, or the supervisor cannot be asked). The null is
    // load-bearing and a surface must keep it apart from zero: 0 is the measured "nobody is here", null is
    // "presence is not knowable for this server" — rendering the second as 0 would put an invented figure
    // in a fleet total, which is exactly the fabrication this API exists not to commit.
    //
    // Counted off the SAME roster GET /servers/{id}/players serves (PlayerHistoryService's projection of
    // the presence echoes), so a per-server card and a fleet aggregate cannot disagree. Observability is
    // PlayerObservability's single answer. It rides the list, the detail AND the `servers` stream, and
    // DomainPump diffs it (unlike the metrics block): a join or a leave is a person-scale event, not a
    // per-second sample, so carrying it costs the topic nothing and it is what lets a fleet total be live.
    int? OnlinePlayers = null,
    // What starting this instance is expected to cost the node in memory (MB), and which figure that is.
    //
    // This is the input to KGSM's memory gate, published so a surface can WARN before a start rather than
    // only reporting the refusal afterwards. It is deliberately the requirement and NOT a verdict: whether
    // there is room depends on what the node has free at the instant the engine looks, which is a moving
    // number this DTO would be stale about the moment it serialized. A client joins this against the
    // owning node's live free memory to show a hint; the ENGINE decides, and its refusal is the answer.
    //
    // Two sources, in the order the gate itself reads them, named in StartMemorySource so a surface can
    // say WHY the figure is what it is:
    //   "cap"       — the instance's own memory_cap_mb, the cgroup ceiling the watchdog enforces. It
    //                 bounds what the node can actually lose, and an operator chose it.
    //   "blueprint" — the blueprint's advisory metadata.min_ram_mb. A vendor estimate, uncurated for many
    //                 games, and capable of overstating what a server really uses — which is exactly why
    //                 a surface should say so rather than presenting it as measured.
    //
    // Both null when neither is declared. That is the common case today and it means the gate cannot
    // answer, so nothing is refused and nothing is warned about — never a substituted default, which
    // would put an invented requirement in front of a real start.
    int? StartMemoryMb = null,
    string? StartMemorySource = null,
    // Which registered library this instance lives in — the named root, resolved by the engine from the
    // absolute path the instance records. An instance under no registered root reports the engine's own
    // word for that, "unregistered": it is on a disk nobody declared, which is a real and recoverable
    // state (a library was deregistered under it) and not a missing value.
    //
    // Empty on a host whose engine predates libraries. It is a NAME, not a path: the path is
    // LibraryPath below, and a surface showing placement shows the name, because that is what an install
    // form takes and what a rename changes.
    string? Library = null,
    // Whether that library's root is reachable: online | offline | unregistered, or null when the
    // registry could not be read (honest unknown — NOT "offline", which would report a disk as gone
    // because one engine invocation failed).
    //
    // A SEPARATE AXIS FROM Status, and never folded into it. Status is run-state, measured by the
    // supervisor; this is whether the files exist to run at all. An offline library makes the run-state
    // unknowable rather than false — nothing can be read through a dangling symlink — so a surface
    // joins the two and shows this one first, exactly as it joins status with metrics. The engine
    // refuses every lifecycle verb on an offline library, so an operator needs to see WHY before the
    // refusal, not after it.
    string? LibraryState = null,
    // The absolute root Library names — what the instance actually records. Carried beside the name
    // so a surface can say where the files are without joining against the host's registry, and so an
    // instance whose library is unregistered can still say which directory it is under.
    string? LibraryPath = null);

/// <summary>
/// A server's operator-authored note — free text an Operator writes for players and teammates
/// (<c>GET/PUT/DELETE /servers/{id}/note</c>, and carried on the <see cref="Server"/> DTO).
/// </summary>
/// <remarks>
/// Lives in the kgsm instance's own <c>.config.ini</c> (kgsm-lib's <c>InstanceNote</c> owns the
/// encoding), so it is engine-owned domain data like every other instance field — not API-local
/// state — and it dies with the instance on uninstall.
/// <para><see cref="UpdatedBy"/>/<see cref="UpdatedAt"/> are honest-null for a note that was never
/// written through a surface (someone hand-edited the config): the body still renders, with no
/// fabricated author or timestamp.</para>
/// </remarks>
/// <param name="Body">The note text, verbatim. Plain text — surfaces must render it as text, never
/// as markup, since it reaches player-facing surfaces.</param>
/// <param name="UpdatedBy">The actor who last wrote it (the same actor string the audit trail
/// carries), or null when unknown.</param>
/// <param name="UpdatedAt">When it was last written (UTC), or null when unknown/unparseable.</param>
public sealed record ServerNote(
    string Body,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// One server's resource sample, mapped 1:1 from the monitor's <c>ServerMetrics</c> with its native
/// units preserved (see <see cref="Server"/>). Present only when the monitor produced a row for this
/// instance this tick.
/// </summary>
/// <param name="CpuPctCore">CPU as a percentage of one core (htop convention); a multi-core server
/// can exceed 100. Deliberately NOT the host's 0–100-across-all-cores figure.</param>
/// <param name="MemBytes">Charged memory in bytes (cgroup <c>memory.current</c> incl. page cache, or
/// summed process RSS for native) — honest, neither a plain <c>ps</c> RSS nor a capped fraction.</param>
/// <param name="IoReadBps">Block-IO read rate (bytes/sec), or <c>null</c> when the io controller is
/// not accounted for this kind (the monitor's own nullable — passed through, never coerced to 0).</param>
/// <param name="IoWriteBps">Block-IO write rate, or <c>null</c> (see <paramref name="IoReadBps"/>).</param>
/// <param name="Pids">Live process/thread count.</param>
/// <param name="DiskBytes">On-disk footprint of the instance's files (bytes) — the monitor's
/// slow-cadence working-dir walk. <c>null</c> when not yet measured / unreadable (passed through,
/// never coerced to 0). A filesystem figure, not a cgroup counter.</param>
/// <param name="RxBps">Per-server network receive rate (bytes/sec). Sourced from the monitor's
/// passive eBPF <c>cgroup/skb</c> byte meter on the instance cgroup (Monitor.Contracts 1.3.0).
/// <c>null</c> when not measurable — meter not set up, the cap absent, or a container instance not
/// under <c>kgsm.slice</c> (passed through, never coerced to 0).</param>
/// <param name="TxBps">Per-server network transmit rate (bytes/sec); same source + honest-null
/// semantics as <paramref name="RxBps"/>.</param>
public sealed record ServerMetricsDto(
    double CpuPctCore,
    long MemBytes,
    long? IoReadBps,
    long? IoWriteBps,
    int Pids,
    long? DiskBytes,
    long? RxBps,
    long? TxBps);

/// <summary>
/// One instance's line in a roster metric frame (<c>metrics.roster</c> on
/// <see cref="Realtime.StreamProtocol.ServersMetricsTopic"/>) — the live counterpart of the
/// <see cref="Server.Metrics"/> + <see cref="Server.DiskBytes"/> pair a REST read hydrates from, in the
/// same two parts and with the same meaning, so a client merges a frame onto a row field-for-field.
/// </summary>
/// <param name="Id">The instance id — the merge key, the same one <see cref="Server.Id"/> carries.</param>
/// <param name="Metrics">This instance's resource sample, or <c>null</c> when the monitor produced no row
/// for it — which is the ordinary case for a stopped server (no cgroup, no process tree). Null is
/// "nothing was measured", NOT "measured zero", and it is never a statement about run-state: status comes
/// from the run-state authority on the <c>servers</c> topic, and a surface joins the two.</param>
/// <param name="DiskBytes">On-disk footprint in bytes, or <c>null</c> when unmeasured. Present
/// independently of <paramref name="Metrics"/> — the whole reason this frame has two parts — because the
/// monitor walks its entire watch-list, so a stopped instance still reports what it occupies.</param>
public sealed record ServerMetricsRow(
    string Id,
    ServerMetricsDto? Metrics,
    long? DiskBytes);

/// <summary>
/// Every server's readout at one instant — the payload of a <c>metrics.roster</c> frame.
/// </summary>
/// <remarks>
/// The array is the roster the monitor knows about, so an id the caller holds and this frame omits means
/// the monitor has nothing at all for it (neither a sample nor a footprint) — not that it is idle. A
/// consumer merges by id and leaves what it isn't told about alone.
/// </remarks>
/// <param name="Servers">One row per instance, in no guaranteed order.</param>
public sealed record ServerMetricsRoster(IReadOnlyList<ServerMetricsRow> Servers);

/// <summary>
/// The honest run-state vocabulary (M1·b, extended with <see cref="Starting"/>). Derived from
/// kgsm-lib's <c>Reading&lt;InstanceRuntimeStatus&gt;</c>: a measured reading maps its boolean
/// <c>Status</c> to <see cref="Running"/>/<see cref="Stopped"/>; any non-measured reading
/// (unavailable / unsupported / skipped, or a missing entry) is <see cref="Unknown"/> — the
/// status was not readable, distinct from a confident "stopped".
/// <para>
/// <see cref="Starting"/> is layered on top of a measured "up" (<c>Status: true</c>) reading: the
/// process has spawned (<c>server.started</c>) but the watchdog hasn't yet confirmed the game
/// finished booting (<c>server.ready</c>). The boolean reading alone cannot distinguish this from
/// <see cref="Running"/> — both observe the process as up — so it is tracked out-of-band by
/// <see cref="Services.Aggregation.InstanceCache"/>'s starting latch, not derivable from the reading
/// by itself. See <c>InstanceCache</c> for the latch design and its reconcile-hazard guard.
/// </para>
/// </summary>
/// <summary>
/// Which figure <see cref="Server.StartMemoryMb"/> is, so a surface can say why — the two differ in how
/// much they should be trusted, and presenting an estimate as a measurement is the whole thing this API
/// avoids.
/// </summary>
public static class StartMemorySource
{
    /// <summary>The instance's own <c>memory_cap_mb</c> — the cgroup ceiling the watchdog writes to
    /// <c>memory.max</c>, so the instance cannot exceed it and an operator chose it deliberately.</summary>
    public const string Cap = "cap";

    /// <summary>The blueprint's advisory <c>metadata.min_ram_mb</c> — vendor-declared, uncurated for many
    /// games, and capable of overstating what a server really uses.</summary>
    public const string Blueprint = "blueprint";
}

public static class ServerStatus
{
    public const string Running = "running";
    public const string Starting = "starting";

    /// <summary>
    /// The watchdog is holding this instance out of service for a span somebody asked for: the process is
    /// down, its desired state is still running, and crash-restart is suppressed for the duration.
    /// </summary>
    /// <remarks>
    /// Its own word rather than <see cref="Stopped"/>, because the two are different facts about a server
    /// and a surface answers them differently: nobody asked for a parked server to be down, nothing went
    /// wrong, and it comes back on its own. Reading it as stopped would report an outage that is not one,
    /// and reading it as <see cref="Unknown"/> would report an absence of measurement where there is a
    /// precise one.
    /// </remarks>
    public const string Maintenance = "maintenance";

    public const string Stopped = "stopped";
    public const string Unknown = "unknown";
}
