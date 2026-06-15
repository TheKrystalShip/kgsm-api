namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The request body for <c>POST /servers/{id}/commands</c> (architecture.html §5·d, M3). The client
/// expresses <em>intent only</em> — a closed, server-defined verb set; an unknown/empty verb is
/// rejected at write time (<c>400</c>). <c>update</c> (long-running, version-changing) is deferred
/// from M3's first cut.
/// <para>
/// <see cref="Origin"/> (M5) is the optional driving <em>surface</em> the client declares (<c>ui</c>,
/// <c>assistant</c>, <c>discord</c>, <c>api</c>) — stamped onto the kgsm command so the resulting event
/// (and its audit row) records which surface drove it. Absent ⇒ <c>api</c> (literally true — it came
/// through the API); an unknown or <c>system</c> value (reserved for autonomous engine actions) is
/// rejected (<c>400</c>). It is independent of the actor (the bearer identity), never derived from it.
/// </para>
/// </summary>
public sealed record CommandRequest(string? Verb, string? Origin = null);

/// <summary>
/// The closed lifecycle verb set the API admits in M3. Server-defined — the client (or, later, the
/// model) cannot invent one.
/// </summary>
public static class CommandVerb
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    public static bool IsKnown(string? verb) => verb is Start or Stop or Restart;
}

/// <summary>
/// A command job (architecture.html §3/§5·d, M3) — returned inline by the <c>202</c> and streamed on
/// the <c>jobs</c> WS topic as <c>job.patch</c>. <see cref="State"/> is the <b>job's own execution
/// lifecycle</b>, NOT the server's display state (which rides the <c>servers</c> topic via
/// <c>server.patch</c> on settle) — a deliberate divergence from the §5·d example's server-shaped
/// <c>state</c>, see <see cref="JobState"/>. In-memory for M3 (SQLite persistence is M5).
/// <see cref="Error"/> carries the engine's real failure detail, set only when <see cref="State"/> is
/// <see cref="JobState.Failed"/> — never a fabricated success.
/// </summary>
public sealed record Job(
    string Id,
    string ServerId,
    string Verb,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt,
    string? Error);

/// <summary>The <c>202 Accepted</c> body: <c>{ "job": { ... } }</c> (architecture.html §3).</summary>
public sealed record CommandAccepted(Job Job);

/// <summary>
/// The job execution lifecycle: <see cref="Queued"/> on accept, <see cref="Running"/> while the verb
/// executes, then a terminal <see cref="Succeeded"/>/<see cref="Failed"/> once it settles and the API
/// has re-checked authoritative run-state.
/// </summary>
public static class JobState
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}
