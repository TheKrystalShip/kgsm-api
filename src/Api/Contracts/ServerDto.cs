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
///   <item><description><c>status</c> is the honest, closed <c>running|starting|stopped|unknown</c>
///     vocabulary — never the aspirational <c>online|offline|updating|crashed|installing</c> (the other
///     transitional states still need the M3 job tracker and crash detection). <c>running</c>/<c>stopped</c>/
///     <c>unknown</c> derive from kgsm-lib's <c>Reading&lt;InstanceRuntimeStatus&gt;</c> as before.
///     <c>starting</c> is a REAL run-state, not a job state: the window between an <c>instance_started</c>
///     event (the process spawned) and the matching <c>instance_ready</c> event (the watchdog's log-scrape
///     confirms the game finished booting) — the boolean status reading alone can't tell these apart (the
///     process is genuinely "up" for both), so <see cref="Services.Aggregation.InstanceCache"/> tracks the
///     window explicitly (a "starting latch") and <c>ServerAggregator.BuildServer</c> is the one place that
///     folds it into <c>status</c>. See <c>InstanceCache</c>'s remarks for why the periodic boolean
///     reconcile must never be allowed to promote <c>starting</c> back to <c>running</c> on its own.</description></item>
///   <item><description><c>metrics</c> preserves the monitor's native units: <c>cpuPctCore</c>
///     (% of <em>one</em> core, can exceed 100 — NOT the host's 0–100), <c>memBytes</c>, nullable
///     <c>io*</c>. The whole block is <c>null</c> when no per-server sample is available.</description></item>
///   <item><description>Omitted as unsourceable: <c>players</c> (no player-query), <c>cpu</c> 0–100,
///     <c>ram.max</c> (no memory limit), <c>ip</c> (not resolved), <c>updatedAt</c> (no state-change
///     tracking until the M2 stream), and the curated <c>game</c> display name (we emit the real
///     <c>blueprint</c> id instead — blueprint metadata curation is deferred, never guessed).</description></item>
///   <item><description><c>startedAt</c> is <strong>wired but honestly null in practice</strong> (see its
///     per-field note): the referenced kgsm-lib cannot parse kgsm's non-ISO <c>start_time</c>. It is
///     present-as-null so the SPA binds a stable shape, and is never fabricated.</description></item>
/// </list>
/// Keys are always present with explicit <c>null</c> values (honest unknown over omission), so the
/// SPA binds a stable shape.
/// </summary>
public sealed record Server(
    // Stable kgsm instance id and the join key (== monitor ServerMetrics.Id == the lib dict key).
    string Id,
    // Display name. Equal to Id today (kgsm has no separate alias); kept distinct for future labels.
    string Name,
    // Blueprint id this instance was installed from (the honest analog of the aspirational `game`).
    string Blueprint,
    // running | starting | stopped | unknown — see ServerStatus. running/stopped/unknown come from
    // Reading<InstanceRuntimeStatus>; starting is the InstanceCache starting-latch window layered on top
    // by ServerAggregator.BuildServer (see the class remarks above).
    string Status,
    // Installed version (InstanceRuntimeStatus.Version.Current). Null when the status is unknown
    // or kgsm reports no version. This is what IS installed; whether something newer exists is the
    // separate UpdateAvailable/LatestVersion pair below.
    string? Version,
    // native | container — the supervision discriminator (Instance.Runtime), lower-cased.
    string Runtime,
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
    // When the running process started (from the instance status reading's process.start_time), or null
    // when stopped/unknown. ⚠ Null in practice today: the referenced kgsm-lib (1.21.0) maps start_time to
    // a System.Text.Json DateTime?, but kgsm emits it as a non-ISO local-time string which STJ cannot parse
    // — a container's start_time (docker's RFC3339 stripped of its offset → "2026-06-16 14:23:01") always
    // throws on deserialization, and a native's `ps lstart` ("Sun Jun 21 20:17:17 2026") throws WHEN a
    // local pid file populates it (a watchdog-spawned native with no local pid file emits null, which parses
    // fine). A throw is swallowed upstream into an empty roster (ServerAggregator.ReadDomain). So the only
    // value that ever reaches this field is a parseable ISO-UTC one. Surfaced honestly (the SPA derives
    // uptime from a start timestamp); the upstream parse gap is flagged for a kgsm-lib/kgsm fix (emit ISO,
    // and/or a start_time converter), out of scope for this read slice. Never a guessed timezone.
    DateTimeOffset? StartedAt = null,
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
    // instance_update_started/finished — an update kgsm is running for some OTHER entrypoint (the CLI, the
    // assistant, the bot). They share one slot, so this is at most one record whoever started it.
    //
    // It is NOT a status. Status stays the run-state vocabulary above; a surface that wants to render
    // "updating" joins the two itself, exactly as it joins status with metrics.
    Job? ActiveJob = null);

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
/// The honest run-state vocabulary (M1·b, extended with <see cref="Starting"/>). Derived from
/// kgsm-lib's <c>Reading&lt;InstanceRuntimeStatus&gt;</c>: a measured reading maps its boolean
/// <c>Status</c> to <see cref="Running"/>/<see cref="Stopped"/>; any non-measured reading
/// (unavailable / unsupported / skipped, or a missing entry) is <see cref="Unknown"/> — the
/// status was not readable, distinct from a confident "stopped".
/// <para>
/// <see cref="Starting"/> is layered on top of a measured "up" (<c>Status: true</c>) reading: the
/// process has spawned (<c>instance_started</c>) but the watchdog hasn't yet confirmed the game
/// finished booting (<c>instance_ready</c>). The boolean reading alone cannot distinguish this from
/// <see cref="Running"/> — both observe the process as up — so it is tracked out-of-band by
/// <see cref="Services.Aggregation.InstanceCache"/>'s starting latch, not derivable from the reading
/// by itself. See <c>InstanceCache</c> for the latch design and its reconcile-hazard guard.
/// </para>
/// </summary>
public static class ServerStatus
{
    public const string Running = "running";
    public const string Starting = "starting";
    public const string Stopped = "stopped";
    public const string Unknown = "unknown";
}
