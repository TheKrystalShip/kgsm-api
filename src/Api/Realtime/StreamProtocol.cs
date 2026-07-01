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
    // --- client -> server command types (the inbound { type, topics[] } envelope) ---
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";

    // --- topics (the M2-scoped, M1-backable set) ---
    /// <summary>All servers' status/roster changes (NOT the 1s metric firehose — see <see cref="ServerMetricsTopic"/>).</summary>
    public const string ServersTopic = "servers";

    /// <summary>One server's per-instance metric ticks: <c>servers/{id}/metrics</c>.</summary>
    public static string ServerMetricsTopic(string id) => $"servers/{id}/metrics";

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

    /// <summary>One server's firewall/ports block: <c>servers/{id}/network</c> (M6·b). The fresh
    /// <see cref="Contracts.ServerNetwork"/> is pushed here after an <c>open_ports</c> command verifies —
    /// kept off the <see cref="ServersTopic"/> so <see cref="ServerPatch"/> stays the frozen M1·b
    /// <c>Server</c> (the same topic-separation discipline as the metric topic). On-demand only — no pump
    /// publishes it (the firewall is socket-activated + idle-exits; a periodic probe would defeat that).</summary>
    public static string ServerNetworkTopic(string id) => $"servers/{id}/network";

    /// <summary>This host's capacity metric ticks: <c>hosts/{hostId}/metrics</c>.</summary>
    public static string HostMetricsTopic(string hostId) => $"hosts/{hostId}/metrics";

    /// <summary>This host's capability status flips: <c>hosts/{hostId}/capabilities</c>.</summary>
    public static string HostCapabilitiesTopic(string hostId) => $"hosts/{hostId}/capabilities";

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

    /// <summary>Topics that require <c>operator</c> to subscribe, refused for a viewer at the socket even though
    /// the <c>/stream</c> handshake is only viewer-gated. Today: the host-logs tail (raw journald can leak
    /// secrets — stricter than the viewer-gated audit feed, matching the REST endpoint's gate).</summary>
    public static bool RequiresOperator(string topic) => IsHostLogsTopic(topic);

    // --- server -> client message types (the `type` field of the { topic, type, data } envelope) ---
    /// <summary>A full honest <c>Server</c> element to merge by id (doc-given). Fired on status/roster change.</summary>
    public const string ServerPatch = "server.patch";
    /// <summary>A roster removal tombstone: <c>data = { id }</c>.</summary>
    public const string ServerRemoved = "server.removed";
    /// <summary>A per-server metric sample (<c>ServerMetricsDto</c>).</summary>
    public const string MetricsTick = "metrics.tick";
    /// <summary>A host capacity sample (<c>HostMetricsDto</c>).</summary>
    public const string HostMetrics = "host.metrics";
    /// <summary>The host's capability block after a status flip (<c>HostCapabilities</c>).</summary>
    public const string CapabilitiesPatch = "capabilities.patch";

    /// <summary>A fresh <see cref="Contracts.ServerNetwork"/> block (M6·b) on <see cref="ServerNetworkTopic"/>,
    /// after an <c>open_ports</c> command re-probes the firewall. Patch-only, supersede-by-latest per server
    /// (the topic is already per-id) — exactly like <see cref="ServerPatch"/>. The data is byte-identical to
    /// the <c>network</c> field a subsequent <c>GET /servers/{id}</c> returns (one shared build path).</summary>
    public const string NetworkPatch = "network.patch";

    /// <summary>The per-connection coalesce key for a server's network block on
    /// <see cref="ServerNetworkTopic"/>: a slow client gets the newest re-probe, never a backlog.</summary>
    public static string ServerNetworkEntityKey(string id) => $"servers-network:{id}";

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

    // --- players (the live roster feed, player-presence-contract.md §5) ---
    /// <summary>Live roster join/leave transitions, host-wide (like <see cref="AuditTopic"/> — one topic,
    /// every server's presence events; the payload's <c>serverId</c> tells them apart, exactly like
    /// <see cref="Contracts.AuditRecord.ServerId"/>). No pump publishes this: <see cref="Services.Players.PlayerRosterService"/>
    /// pushes directly from the same join/leave event handlers that write the <c>player.join</c>/<c>player.leave</c>
    /// audit rows.</summary>
    public const string PlayersTopic = "players";
    /// <summary>A session joined a server's roster: <c>data = { serverId, player }</c> (player = the
    /// <see cref="Contracts.RosterPlayer"/> shape <c>GET /servers/{id}/players</c> returns).</summary>
    public const string PlayersJoin = "players.join";
    /// <summary>A session left a server's roster: <c>data = { serverId, player }</c> — same shape as
    /// <see cref="PlayersJoin"/> (the player's LAST known state at leave), so the client can render a
    /// "just left" line without a second lookup.</summary>
    public const string PlayersLeave = "players.leave";
    /// <summary>A server's WHOLE roster was cleared (instance stop/start/restart — a fresh server
    /// session invalidates every prior one): <c>data = { serverId }</c>, no per-session payload. The
    /// client drops every entry it holds for that server rather than waiting for N individual
    /// <see cref="PlayersLeave"/> frames that will never arrive (the underlying sessions vanish without
    /// emitting their own leave lines — player-presence-contract.md §5).</summary>
    public const string PlayersReset = "players.reset";

    /// <summary>The per-connection coalesce key for a roster transition on <see cref="PlayersTopic"/>: a
    /// join and a later leave for the SAME <c>(serverId, sessionKey)</c> share a slot, so a leave correctly
    /// supersedes a still-queued join for that session (the <see cref="ServerEntityKey"/>/<see cref="ServerRemoved"/>
    /// precedent) — a slow client never double-renders a session that already left.</summary>
    public static string PlayerEntityKey(string serverId, string sessionKey) => $"players:{serverId}:{sessionKey}";

    /// <summary>The per-connection coalesce key for a <see cref="PlayersReset"/> frame: keyed on the
    /// server alone (no session), so a repeat reset for the same server collapses to the latest — a
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
    /// The per-connection coalesce key for a server entity on the <see cref="ServersTopic"/>. A patch
    /// and a later removal for the same id share this key, so the newer supersedes any unsent older
    /// (a removal correctly overrides a queued patch, and vice-versa) — see <c>StreamConnection</c>.
    /// </summary>
    public static string ServerEntityKey(string id) => $"servers:{id}";
}
