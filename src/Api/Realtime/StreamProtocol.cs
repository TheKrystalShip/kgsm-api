namespace TheKrystalShip.Api.Realtime;

/// <summary>
/// The single source of truth for the realtime wire vocabulary (M2) — every topic name and
/// message <c>type</c> string lives here, never inlined as a magic string at a call site.
/// The contract is frozen in <c>PLAN.md §6</c> and reconciled against <c>architecture.html §3·b</c>.
/// <para>
/// Adding a topic/type is a one-line, additive change here (M3 brings <c>jobs</c>, M5 <c>audit</c>,
/// etc.); subscribers to a not-yet-implemented topic are accepted and simply receive nothing until
/// a pump publishes it. Only <see cref="ServerPatch"/> is doc-given; the other message types are
/// ours, negotiated honest-vs-aspirational exactly like the M1·b <c>Server</c> DTO and signed off
/// before freezing.
/// </para>
/// </summary>
public static class StreamProtocol
{
    // --- topics (the scoped, backable set) ---
    /// <summary>All servers' status/roster changes (NOT the 1s metric firehose — see <see cref="ServerMetricsTopic"/>).</summary>
    public const string ServersTopic = "servers";

    /// <summary>One server's per-instance metric ticks: <c>servers/{id}/metrics</c>.</summary>
    public static string ServerMetricsTopic(string id) => $"servers/{id}/metrics";

    /// <summary>Every server's resource readout in ONE frame: <c>servers/metrics</c> — what a roster of
    /// cards reads, as against <see cref="ServerMetricsTopic"/>, which is one chart's feed.
    /// <para>The distinction is not cosmetic. A card grid wants a number per server; subscribing to N
    /// per-server topics is N subscriptions for a screen that shows one figure each, and a client whose
    /// transport opens a connection per resource-scoped topic cannot do it at all. So this topic carries
    /// the whole roster — including instances that are stopped, which have no metrics row and a disk
    /// footprint all the same — on a slower, card-shaped cadence (see <see cref="MetricsPump"/>).</para></summary>
    public const string ServersMetricsTopic = "servers/metrics";

    /// <summary>One server's live console (stdout) tail: <c>servers/{id}/console</c> (#8). A
    /// <strong>follow-only</strong> topic — the client hydrates scrollback via the REST
    /// <c>GET /servers/{id}/console?tail=N</c> and applies the live lines pushed here from the next line on
    /// (the patch-only, no-snapshot-on-subscribe rule, §3·j). <see cref="ConsoleBridgeManager"/> opens exactly
    /// one shared watchdog tail-bridge per instance while it has subscribers and closes it when the last one
    /// leaves. NATIVE instances only (a container's stdout is Docker's — out of scope).</summary>
    public static string ServerConsoleTopic(string id) => $"servers/{id}/console";

    /// <summary>Does <paramref name="topic"/> name some <c>servers/{id}/console</c> topic? Lets the bridge
    /// manager gate all enumeration on <see cref="StreamHub.AnySubscription"/> so an idle stream costs
    /// nothing (no watchdog list, no bridges) — the metric-pump subscriber-gate discipline.</summary>
    public static bool IsServerConsoleTopic(string topic) =>
        topic.StartsWith("servers/", StringComparison.Ordinal) && topic.EndsWith("/console", StringComparison.Ordinal);

    /// <summary>This host's capacity metric ticks: <c>hosts/{hostId}/metrics</c>.</summary>
    public static string HostMetricsTopic(string hostId) => $"hosts/{hostId}/metrics";

    /// <summary>This host's capability status flips: <c>hosts/{hostId}/capabilities</c>.</summary>
    public static string HostCapabilitiesTopic(string hostId) => $"hosts/{hostId}/capabilities";

    /// <summary>This host's leaf-service state changes: <c>hosts/{hostId}/services</c> (the live companion
    /// to the REST <c>GET /hosts/{id}/services</c>). The canonical source for service health and running
    /// status — the client hydrates the initial list via REST and applies <see cref="ServicePatch"/> frames
    /// from here on. Operator-gated like the REST endpoint (systemd unit names, pids, memory).</summary>
    public static string HostServicesTopic(string hostId) => $"hosts/{hostId}/services";

    /// <summary>This host's live aggregated leaf logs: <c>hosts/{hostId}/logs</c> (the live-tail companion to
    /// the REST <c>GET /hosts/{id}/logs</c>). A <strong>follow-only</strong>, <strong>operator-gated</strong>
    /// topic — the client hydrates history via REST and applies live lines from here on (patch-only, §3·j).
    /// One shared <c>journalctl -f</c> per host (<see cref="JournalFollowBridge"/>) feeds it while it has
    /// subscribers; raw journald can carry secrets, so <see cref="RequiresOperator"/> refuses a viewer's
    /// subscribe at the socket (defense-in-depth on top of the operator-gated REST endpoint).</summary>
    public static string HostLogsTopic(string hostId) => $"hosts/{hostId}/logs";

    /// <summary>Does <paramref name="topic"/> name some <c>hosts/{id}/logs</c> topic? (the bridge's idle-gate +
    /// the operator predicate).</summary>
    public static bool IsHostLogsTopic(string topic) =>
        topic.StartsWith("hosts/", StringComparison.Ordinal) && topic.EndsWith("/logs", StringComparison.Ordinal);

    /// <summary>Does <paramref name="topic"/> name some <c>hosts/{id}/services</c> topic? (the services pump's
    /// idle-gate + the operator predicate).</summary>
    public static bool IsHostServicesTopic(string topic) =>
        topic.StartsWith("hosts/", StringComparison.Ordinal) && topic.EndsWith("/services", StringComparison.Ordinal);

    /// <summary>Topics that require <c>operator</c> to subscribe, refused for a viewer at the socket even though
    /// the <c>/stream</c> handshake is only viewer-gated. Today: the host-logs tail (raw journald can leak
    /// secrets) and the host-services board (systemd unit names, pids, memory) — both stricter than the
    /// viewer-gated audit feed, matching their REST endpoint's operator gate.</summary>
    public static bool RequiresOperator(string topic) => IsHostLogsTopic(topic) || IsHostServicesTopic(topic);

    // --- server -> client message types (the `type` field of the { topic, type, data } envelope) ---
    /// <summary>A full honest <c>Server</c> element to merge by id (doc-given). Fired on status/roster change.</summary>
    public const string ServerPatch = "server.patch";
    /// <summary>A roster removal tombstone: <c>data = { id }</c>.</summary>
    public const string ServerRemoved = "server.removed";
    /// <summary>A per-server metric sample (<c>ServerMetricsDto</c>).</summary>
    public const string MetricsTick = "metrics.tick";
    /// <summary>Every server's readout at one instant on <see cref="ServersMetricsTopic"/>:
    /// <c>data</c> is a <see cref="Contracts.ServerMetricsRoster"/>. Supersede-by-latest like the other
    /// metric frames — the client applies the newest and merges each row by id.</summary>
    public const string MetricsRoster = "metrics.roster";

    /// <summary>The per-connection coalesce key for the roster metric frame: one key for the whole
    /// topic, so a slow client gets the newest readout rather than a queue of superseded ones. There is
    /// no per-server key here on purpose — the frame IS the roster, and half of an old one merged
    /// under half of a new one is a picture of no instant at all.</summary>
    public const string ServersMetricsEntityKey = "servers-metrics";
    /// <summary>A host capacity sample (<c>HostMetricsDto</c>).</summary>
    public const string HostMetrics = "host.metrics";
    /// <summary>The host's capability block after a status flip (<c>HostCapabilities</c>).</summary>
    public const string CapabilitiesPatch = "capabilities.patch";

    /// <summary>A leaf service's state changed on the <see cref="HostServicesTopic"/>: <c>data</c> is a full
    /// <see cref="Contracts.LeafService"/> element (the same shape the REST <c>GET /hosts/{id}/services</c>
    /// returns) — merged by the client by <c>id</c>. Emitted when systemd state, health, or provisioning
    /// flips for any leaf in the <see cref="Leaves.LeafCatalog"/>.</summary>
    public const string ServicePatch = "service.patch";

    // --- console (#8 — the follow-only stdout stream) ---
    /// <summary>One live console line on a <see cref="ServerConsoleTopic"/>: <c>data = { id, seq, line }</c>.
    /// <para><b>Best-effort tail (the honest contract, mirroring the audit-topic precedent).</b> Lines may
    /// drop on a slow/torn client — the per-line coalesce key (<see cref="ConsoleEntityKey"/>) bounds the
    /// outbound queue and a stalled send is torn down (<c>StreamConnection.SendTimeout</c>); the client then
    /// re-hydrates recent context via <c>GET /servers/{id}/console?tail=N</c> on reconnect. The durable
    /// record is the watchdog's LogFile. Console output is NEVER fabricated.</para></summary>
    public const string ConsoleLine = "console.line";

    /// <summary>The per-connection coalesce key for a console line: <c>console:{id}:{seq}</c> — <b>UNIQUE per
    /// line</b> (the <c>audit</c>-append precedent, NOT the supersede-by-latest server/metric key), so distinct
    /// lines each occupy their own outbound slot and never collapse into the latest. A slow client drops some
    /// lines under backpressure but never silently fuses two into one.</summary>
    public static string ConsoleEntityKey(string id, long seq) => $"console:{id}:{seq}";

    // --- host logs (the live-tail companion to GET /hosts/{id}/logs) ---
    /// <summary>One live log line on a <see cref="HostLogsTopic"/>: <c>data</c> is the same
    /// <see cref="Contracts.LogLine"/> shape the REST endpoint returns (one shared wire shape, so the client
    /// adapts WS and REST lines identically). Best-effort tail (the console/audit precedent): under
    /// backpressure a slow client drops <em>some</em> lines but they never fuse — the coalesce key is the
    /// line's unique journald cursor (<see cref="HostLogEntityKey"/>). The durable record is the journal; the
    /// client re-hydrates via REST on reconnect.</summary>
    public const string LogLine = "log.line";

    /// <summary>The per-connection coalesce key for a live log line: the entry's unique journald cursor
    /// (<c>logs:{cursor}</c>) — UNIQUE per line (the audit/console precedent, NOT supersede-by-latest), so
    /// distinct lines each occupy their own outbound slot and never collapse into the latest.</summary>
    public static string HostLogEntityKey(string cursor) => $"logs:{cursor}";

    // --- jobs (M3 — the command write path) ---
    /// <summary>Command/job progress + completion (host-wide): <c>jobs</c> (architecture.html §5·d).</summary>
    public const string JobsTopic = "jobs";
    /// <summary>
    /// A full <see cref="Contracts.Job"/> on every state transition, merged by id (patch-only, exactly like
    /// <see cref="ServerPatch"/>). <c>job.state</c> is the <em>job's own</em> lifecycle
    /// (<c>queued→running→succeeded|failed</c>); the affected server's authoritative status rides
    /// <see cref="ServersTopic"/> via <see cref="ServerPatch"/> on settle — a deliberate divergence from the
    /// §5·d example's server-shaped <c>state</c>, the same topic-separation discipline as the metric topics.
    /// </summary>
    public const string JobPatch = "job.patch";

    /// <summary>
    /// The per-connection coalesce key for a job on the <see cref="JobsTopic"/>: a slow client gets the
    /// newest transition for a job id, never an unbounded backlog of its intermediate states.
    /// </summary>
    public static string JobEntityKey(string id) => $"jobs:{id}";

    // --- batches (one verb across a set of servers) ---
    /// <summary>Batch progress (host-wide): <c>batches</c>. The roll-up above <see cref="JobsTopic"/> —
    /// a batch's members each publish their own <see cref="JobPatch"/>, and this says where the batch as
    /// a whole stands without a client having to reassemble it from N job frames.</summary>
    public const string BatchesTopic = "batches";

    /// <summary>
    /// A full <see cref="Contracts.BatchView"/> on every member transition, merged by id (patch-only,
    /// like every other topic here). Viewer-gated, matching the REST reads: a batch names servers and
    /// verbs, which is what the roster already shows anyone who can see the host.
    /// </summary>
    public const string BatchPatch = "batch.patch";

    /// <summary>The per-connection coalesce key for a batch: a slow client gets the newest standing of a
    /// batch, never a backlog of every member transition that produced it.</summary>
    public static string BatchEntityKey(string id) => $"batches:{id}";

    // --- audit (M5 — the append-only action log) ---
    /// <summary>Newly-appended audit records (host-wide): <c>audit</c> (architecture.html §3·d).</summary>
    public const string AuditTopic = "audit";
    /// <summary>
    /// A single appended <see cref="Contracts.AuditRecord"/> (the client prepends it — events are
    /// immutable, never edited). Unlike the metric/status patches this is <em>not</em> a
    /// supersede-by-latest patch: each append is a distinct fact, so its coalesce key is the unique
    /// event id (see <see cref="AuditEntityKey"/>) and appends never collapse into one another.
    /// </summary>
    public const string AuditAppend = "audit.append";

    /// <summary>
    /// The per-connection coalesce key for an audit record on the <see cref="AuditTopic"/>: the unique
    /// event id, so distinct appends each occupy their own outbound slot (never supersede each other).
    /// </summary>
    public static string AuditEntityKey(string id) => $"audit:{id}";

    // --- players (the permanent player roster, player-presence-contract.md §5) ---
    /// <summary>Player roster transitions, host-wide (like <see cref="AuditTopic"/> — one topic,
    /// every server's presence events; the payload's <c>serverId</c> tells them apart, exactly like
    /// <see cref="Contracts.AuditRecord.ServerId"/>). Published by <see cref="Services.Players.PlayerHistoryService"/>
    /// from the same join/leave event handlers that write the <c>player.join</c>/<c>player.leave</c>
    /// audit rows — the history service is the authority; the in-memory roster is session-level dedup only.</summary>
    public const string PlayersTopic = "players";
    /// <summary>A player came online: <c>data = { serverId, player }</c> (player = the
    /// <see cref="Contracts.RosterPlayer"/> shape with <c>status</c>, <c>firstSeen</c>, <c>lastSeen</c>,
    /// <c>banReason</c> — the full permanent roster record).</summary>
    public const string PlayersJoin = "players.join";
    /// <summary>A player went offline: <c>data = { serverId, player }</c> — same shape as
    /// <see cref="PlayersJoin"/> (the player's LAST known state at leave), so the client can update
    /// the status without a second lookup.</summary>
    public const string PlayersLeave = "players.leave";
    /// <summary>A server's WHOLE roster was cleared (instance stop/start/restart — a fresh server
    /// session invalidates every prior one): <c>data = { serverId }</c>, no per-player payload. All
    /// players for that server are set to <c>offline</c> in the history. The client updates every
    /// entry it holds for that server rather than waiting for N individual
    /// <see cref="PlayersLeave"/> frames that will never arrive (the underlying sessions vanish without
    /// emitting their own leave lines — player-presence-contract.md §5).</summary>
    public const string PlayersReset = "players.reset";
    /// <summary>A player was banned: <c>data = { serverId, player }</c> — same shape with
    /// <c>status: "banned"</c> and <c>banReason</c> populated.</summary>
    public const string PlayersBan = "players.ban";

    /// <summary>The per-connection coalesce key for a roster transition on <see cref="PlayersTopic"/>: a
    /// join and a later leave for the SAME <c>(serverId, playerIdentity)</c> share a slot, so a leave correctly
    /// supersedes a still-queued join for that player — a slow client never double-renders a player that
    /// already left. Player-level (not session-level) because the permanent roster deduplicates by
    /// <c>playerIdentity</c>, not <c>sessionKey</c>.</summary>
    public static string PlayerEntityKey(string serverId, string playerIdentity) => $"players:{serverId}:{playerIdentity}";

    /// <summary>The per-connection coalesce key for a <see cref="PlayersReset"/> frame: keyed on the
    /// server alone (no player), so a repeat reset for the same server collapses to the latest — a
    /// stacked-up reset carries no additional information over the newest one.</summary>
    public static string PlayerResetEntityKey(string serverId) => $"players-reset:{serverId}";

    // --- alerts (M6·a — the condition-mirror feed) ---
    /// <summary>Live problem conditions, host-wide: <c>alerts</c> (architecture.html §3·c).</summary>
    public const string AlertsTopic = "alerts";
    /// <summary>A new condition starts firing, OR a re-push of the full <see cref="Contracts.Alert"/>
    /// record (e.g. to flip <c>escalated</c>). The client upserts by id.</summary>
    public const string AlertRaise = "alert.raise";
    /// <summary>A condition cleared — carries <c>{ id, resolution }</c> (<see cref="Contracts.AlertResolved"/>).
    /// The client stamps <c>resolvedAt</c> and moves the record to the 24h rear-view.</summary>
    public const string AlertResolve = "alert.resolve";
    /// <summary>The thing was never an actionable condition (or its subject is gone) — carries <c>{ id }</c>
    /// (<see cref="Contracts.AlertRetracted"/>). The client drops it: no rear-view, no resolution.</summary>
    public const string AlertRetract = "alert.retract";

    /// <summary>
    /// The per-connection coalesce key for an alert on the <see cref="AlertsTopic"/>: the alert id, so all
    /// three message kinds for one condition share a slot — a <c>resolve</c>/<c>retract</c> correctly
    /// supersedes a still-queued <c>raise</c> for that id, exactly like <see cref="ServerRemoved"/> overrides
    /// a queued <see cref="ServerPatch"/>. A torn-down slow client re-hydrates the firing set via
    /// <c>GET /alerts</c> on reconnect (§3·j), so coalescing never loses durable truth.
    /// </summary>
    public static string AlertEntityKey(string id) => $"alerts:{id}";

    /// <summary>
    /// The per-connection coalesce key for a leaf service on the <see cref="HostServicesTopic"/>: a
    /// <see cref="ServicePatch"/> for the same leaf id supersedes any earlier queued patch — a slow client
    /// gets the latest state per leaf, never a backlog. Distinct from the audit/console append precedent
    /// (which are unique-per-event) — service state is supersede-by-latest.
    /// </summary>
    public static string ServiceEntityKey(string leafId) => $"services:{leafId}";

    /// <summary>
    /// The per-connection coalesce key for a server entity on the <see cref="ServersTopic"/>. A patch
    /// and a later removal for the same id share this key, so the newer supersedes any unsent older
    /// (a removal correctly overrides a queued patch, and vice-versa) — see <c>StreamConnection</c>.
    /// </summary>
    public static string ServerEntityKey(string id) => $"servers:{id}";
}
