namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The request body for <c>POST /servers/{id}/commands</c> (architecture.html §5·d, M3). The client
/// expresses <em>intent only</em> — a closed, server-defined verb set
/// (<c>start</c>/<c>stop</c>/<c>restart</c>/<c>update</c>/<c>open_ports</c>); an unknown/empty verb is
/// rejected at write time (<c>400</c>). <c>update</c> (long-running, version-changing) joined the set in
/// the Tier-1 ops slice.
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
/// The request body for <c>POST /servers</c> (architecture.html §3·h, M8·b) — the panel's one
/// <em>create</em> operation. The contract is deliberately lopsided: the client may send the whole
/// install form, but the installer (kgsm) needs exactly one thing — which <strong>blueprint</strong>.
/// <list type="bullet">
///   <item><description><b>Required:</b> <see cref="Blueprint"/> — the library id the user picked.</description></item>
///   <item><description><b>Honored today:</b> <see cref="Name"/> — passed to kgsm as the instance
///     <em>name</em> (not a free-text display label: kgsm validates it as an instance id and falls back
///     to an auto-generated <c>blueprint-suffix</c> if it isn't a usable unique name). <see cref="Origin"/>
///     (the driving surface, like <see cref="CommandRequest.Origin"/>) is stamped onto the engine call so
///     the resulting <c>instance_installed</c> event — and its <c>server.install</c> audit row — records it.
///     <see cref="Port"/> — the install form's Game Port; validated 1-65535 and passed to kgsm as
///     <c>install --port</c>, overriding the blueprint's primary game port for the new instance (null keeps
///     the blueprint default).</description></item>
///   <item><description><b>Reserved — accepted &amp; ignored (additive-only, §3·h):</b> everything else.
///     Sending them keeps the schema forward-compatible so the backend can grow into a field with no
///     client change and no version bump; until then they are <em>inert</em> (never silently half-applied).</description></item>
/// </list>
/// Install is async: the endpoint returns a <see cref="Job"/> (not a server). When it completes the new
/// server appears on <c>/servers</c> with a backend-assigned id and a <c>server.install</c> audit entry.
/// </summary>
public sealed record InstallRequest(
    string? Blueprint,
    string? Name = null,
    string? Origin = null,
    // ---- reserved: accepted & stored, not yet acted on (§3·h additive-only) ----
    string? HostId = null,
    string? Version = null,
    int? Port = null,
    int? QueryPort = null,
    int? Slots = null,
    string? Dir = null,
    string? Password = null,
    bool? Autostart = null);

/// <summary>
/// The closed lifecycle verb set the API admits in M3 (+ <c>update</c> + <c>open_ports</c>). Server-defined —
/// the client (or, later, the model) cannot invent one. <see cref="Install"/>/<see cref="Uninstall"/>
/// (M8·b) and <see cref="BackupCreate"/>/<see cref="BackupRestore"/> (Tier-1 ops) are <em>not</em> part of
/// <see cref="IsKnown"/>: they are NOT <c>POST /servers/{id}/commands</c> verbs (install creates a server /
/// targets the collection; restore carries a <c>backupName</c> param) — they have dedicated endpoints
/// (<c>POST /servers</c>, <c>DELETE /servers/{id}</c>, <c>POST /servers/{id}/backups</c>,
/// <c>POST /servers/{id}/backups/restore</c>). These constants only name the <see cref="Job.Verb"/>
/// so they reuse the shared <c>JobRegistry</c>/<c>CommandRunner</c> (one job model, one in-flight slot per
/// server, one verify discipline).
/// </summary>
public static class CommandVerb
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    /// <summary>
    /// Update an instance to the latest version (Tier-1 ops — <c>POST /servers/{id}/commands</c>).
    /// Long-running and version-changing, so it rides the same job machinery as the lifecycle verbs (a
    /// <c>202</c> + a job, progress on the <c>jobs</c> topic). It does NOT route through
    /// <c>ILifecycleService</c> — kgsm exposes update on <c>IInstanceService.Update</c> — so the runner has
    /// a dedicated case (mirroring install/uninstall). kgsm refuses an update on a RUNNING instance, surfaced
    /// synchronously by <see cref="CommandGate"/> as a <c>409</c> (the engine refusal is the backstop).
    /// Audited via the echo path (kgsm's <c>instance_version_updated</c> → <c>server.update</c>), NOT a
    /// direct write.
    /// </summary>
    public const string Update = "update";

    /// <summary>Install a new instance from a blueprint (M8·b — <c>POST /servers</c>). NOT in
    /// <see cref="IsKnown"/>; the job's <see cref="Job.ServerId"/> is the backend-assigned instance id.</summary>
    public const string Install = "install";

    /// <summary>Uninstall an instance (M8·b — <c>DELETE /servers/{id}</c>). NOT in <see cref="IsKnown"/>.</summary>
    public const string Uninstall = "uninstall";

    /// <summary>Create a backup of an instance (Tier-1 ops — <c>POST /servers/{id}/backups</c>). NOT in
    /// <see cref="IsKnown"/>; it has a dedicated route (collection target) but reuses the job machinery.</summary>
    public const string BackupCreate = "backup_create";

    /// <summary>Restore an instance from a named backup (Tier-1 ops — <c>POST /servers/{id}/backups/restore</c>).
    /// NOT in <see cref="IsKnown"/>; carries a <c>backupName</c> param, so a dedicated route, reusing the job
    /// machinery (the install/uninstall pattern — verbs are param-less, this is not).</summary>
    public const string BackupRestore = "backup_restore";

    /// <summary>
    /// Open this server's required host-firewall ports (M6·b, architecture.html §3·g). <strong>Intent
    /// only — the client sends NO port list</strong>: the server derives the target set from the
    /// instance's own <c>Instance.Ports</c> (accepting a client list would let the browser open
    /// anything). Always admissible (no run-state no-op — opening ports is declarative/idempotent),
    /// audited by a <em>direct</em> <c>network.ports.open</c> write (kgsm runs nothing on the
    /// <c>IFirewallService</c> path → no event echo → no double-write), verified by a firewall re-probe
    /// pushed on <c>servers/{id}/network</c>.
    /// </summary>
    public const string OpenPorts = "open_ports";

    public static bool IsKnown(string? verb) => verb is Start or Stop or Restart or Update or OpenPorts;
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
