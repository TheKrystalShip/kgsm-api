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

    /// <summary>This host's capacity metric ticks: <c>hosts/{hostId}/metrics</c>.</summary>
    public static string HostMetricsTopic(string hostId) => $"hosts/{hostId}/metrics";

    /// <summary>This host's capability status flips: <c>hosts/{hostId}/capabilities</c>.</summary>
    public static string HostCapabilitiesTopic(string hostId) => $"hosts/{hostId}/capabilities";

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

    /// <summary>
    /// The per-connection coalesce key for a server entity on the <see cref="ServersTopic"/>. A patch
    /// and a later removal for the same id share this key, so the newer supersedes any unsent older
    /// (a removal correctly overrides a queued patch, and vice-versa) — see <c>StreamConnection</c>.
    /// </summary>
    public static string ServerEntityKey(string id) => $"servers:{id}";
}
