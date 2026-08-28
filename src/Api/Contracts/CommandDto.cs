namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The request body for <c>POST /servers/{id}/commands</c> (architecture.html §5·d, M3). The client
/// expresses <em>intent only</em> — a closed, server-defined verb set
/// (<c>start</c>/<c>stop</c>/<c>restart</c>/<c>update</c>); an unknown/empty verb is
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
/// <param name="Force">
/// Override the engine's node-capacity check — <c>start</c> only. KGSM refuses a start that would
/// leave the node with less free memory than its configured floor, judging the instance against its
/// own <c>memory_cap_mb</c> or, failing that, its blueprint's advisory <c>min_ram_mb</c>. That
/// fallback is a vendor estimate and can overstate what a game really uses, so an operator who knows
/// better can say so. Absent ⇒ false: the protection is what a caller gets by not asking.
/// <para>
/// Operator, not admin. The judgement it takes — "this blueprint's figure is wrong for this server" —
/// is one anyone who runs these servers day to day is in a position to make, and the tier that may
/// start a server is the same tier that may decide it fits.
/// </para>
/// It does not create memory. Forcing a start the node genuinely cannot fit invites the OOM killer,
/// which picks by its own heuristic and may take down a different server, or the watchdog supervising
/// them all.
/// </param>
public sealed record CommandRequest(string? Verb, string? Origin = null, bool Force = false);

/// <summary>
/// The request body for <c>POST /servers</c> (architecture.html §3·h, M8·b) — the panel's one
/// <em>create</em> operation. The contract is deliberately lopsided: the client may send the whole
/// install form, but the installer (kgsm) needs exactly one thing — which <strong>blueprint</strong>.
/// <list type="bullet">
///   <item><description><b>Required:</b> <see cref="Blueprint"/> — the library id the user picked.</description></item>
///   <item><description><b>Honored today:</b> <see cref="Name"/> — the free-text label the new server is
///     read by, stored as the instance's <c>display_name</c> and never part of a path.
///     <see cref="Id"/> — the instance id, which a caller only names when it has to know it in advance;
///     leaving it out is what a human-facing form does, and the backend assigns one (see below).
///     <see cref="Origin"/>
///     (the driving surface, like <see cref="CommandRequest.Origin"/>) is stamped onto the engine call so
///     the resulting <c>server.installed</c> event — and its <c>server.install</c> audit row — records it.
///     <see cref="Port"/> — the install form's Game Port; validated 1-65535 and passed to kgsm as
///     <c>install --port</c>, overriding the blueprint's primary game port for the new instance (null keeps
///     the blueprint default). <see cref="Autostart"/> — when <c>true</c>, starts the server
///     immediately after install completes (one-shot start, not watchdog boot-autostart); defaults to
///     <c>false</c> when absent.</description></item>
///   <item><description><b>Reserved — accepted &amp; ignored (additive-only, §3·h):</b> everything else not
///     listed above. Sending them keeps the schema forward-compatible so the backend can grow into a
///     field with no client change and no version bump; until then they are <em>inert</em> (never silently
///     half-applied).</description></item>
/// </list>
/// Install is async: the endpoint returns a <see cref="Job"/> (not a server). When it completes the new
/// server appears on <c>/servers</c> with a backend-assigned id and a <c>server.install</c> audit entry.
/// </summary>
/// <param name="Name">
/// The label the new server is read by — free text, stored as the instance's <c>display_name</c>. It
/// decorates and never identifies: spaces, punctuation and emoji are all legal, two servers may carry the
/// same one, and changing it later is <c>PUT /servers/{id}/display-name</c>. Absent leaves the instance
/// reading as its id.
/// </param>
/// <param name="Id">
/// The instance id to install under — the durable key every path, event and downstream store uses, and
/// immutable once installed. Absent is the normal case and the one a create form sends: the backend
/// assigns one, deriving a path-safe slug from <paramref name="Name"/> when that yields a free id and
/// falling back to the engine's own <c>blueprint</c>/<c>blueprint-NN</c> otherwise. Naming it is for a
/// caller that must know the id before the install finishes; it is validated against the engine's charset
/// (<c>^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$</c>) and the live roster, and a bad or taken one is a <c>400</c>
/// rather than an adjusted id the caller never asked for.
/// </param>
/// <param name="Library">
/// Which registered library to place the instance in — the named root, not a path. Absent leaves the
/// choice to the engine's own resolution (its configured default library, or the sole registered one).
/// A name this host does not carry, or one whose root is offline, is rejected up front with a
/// <c>400</c> rather than becoming a job that fails a moment later.
/// </param>
public sealed record InstallRequest(
    string? Blueprint,
    string? Name = null,
    string? Origin = null,
    int? Port = null,
    bool? Autostart = null,
    string? Library = null,
    string? Id = null,
    // ---- reserved: accepted & stored, not yet acted on (§3·h additive-only) ----
    string? HostId = null,
    string? Version = null,
    int? QueryPort = null,
    int? Slots = null,
    string? Dir = null,
    string? Password = null);

/// <summary>
/// The <c>POST /servers/{id}/move</c> body — which library to move the instance's files into.
/// </summary>
/// <param name="Library">
/// The target library, by registered name. Required. A name this host does not carry, one whose root
/// is not reachable, and the one the instance is already in are each refused synchronously, so the
/// form answers beside its own selector rather than producing a job that fails a moment later
/// somewhere nobody is looking.
/// </param>
/// <param name="Origin">The declared driving surface (<c>ui|assistant|discord|api</c>).</param>
public sealed record MoveServerRequest(string? Library, string? Origin = null);

/// <summary>
/// The closed lifecycle verb set the API admits. Server-defined —
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
    /// Audited via the echo path (kgsm's <c>server.updated</c> → <c>server.update</c>), NOT a
    /// direct write.
    /// </summary>
    public const string Update = "update";

    /// <summary>Install a new instance from a blueprint (M8·b — <c>POST /servers</c>). NOT in
    /// <see cref="IsKnown"/>; the job's <see cref="Job.ServerId"/> is the backend-assigned instance id.</summary>
    public const string Install = "install";

    /// <summary>Uninstall an instance (M8·b — <c>DELETE /servers/{id}</c>). NOT in <see cref="IsKnown"/>.</summary>
    public const string Uninstall = "uninstall";

    /// <summary>
    /// Move an instance's files into another library (<c>POST /servers/{id}/move</c>). NOT in
    /// <see cref="IsKnown"/>; it carries a target library, so a dedicated route reusing the job
    /// machinery (the install/backup_restore pattern — the plain verbs are param-less).
    /// </summary>
    /// <remarks>
    /// <b>Its job is what a surface renders "moving" from.</b> The engine starts the instance once on
    /// the new path to confirm it runs there, so a <c>server.started</c> and a
    /// <c>server.stopped</c> land partway through with no bracket around them — a card reading
    /// run-state alone flickers "running" mid-move. The job holds the server's in-flight slot for the
    /// whole operation, which is the span a surface should trust instead.
    /// </remarks>
    public const string Move = "move";

    /// <summary>Create a backup of an instance (Tier-1 ops — <c>POST /servers/{id}/backups</c>). NOT in
    /// <see cref="IsKnown"/>; it has a dedicated route (collection target) but reuses the job machinery.</summary>
    public const string BackupCreate = "backup_create";

    /// <summary>Restore an instance from a named backup (Tier-1 ops — <c>POST /servers/{id}/backups/restore</c>).
    /// NOT in <see cref="IsKnown"/>; carries a <c>backupName</c> param, so a dedicated route, reusing the job
    /// machinery (the install/uninstall pattern — verbs are param-less, this is not).</summary>
    public const string BackupRestore = "backup_restore";

    /// <summary>Delete one named backup (Tier-1 ops — <c>DELETE /servers/{id}/backups/{backupId}</c>). NOT in
    /// <see cref="IsKnown"/>. Unlike the other backup verbs this one is <em>synchronous</em>: it borrows the
    /// registry's per-server in-flight slot purely as a mutex — a restore reads the bytes a delete removes —
    /// and settles within the request, so no job is ever handed to a caller to await.</summary>
    public const string BackupDelete = "backup_delete";

    public static bool IsKnown(string? verb) => verb is Start or Stop or Restart or Update;
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
/// <param name="BatchId">The batch this job belongs to, when one issued it. A job is the same thing
/// whether a person clicked once or asked for twenty at a time — this names the record that ties it
/// to its siblings, and is null for a hand-issued command.</param>
/// <param name="QueuedPosition">The job's stable 1-based ordinal within its batch, so ten jobs all
/// reading <c>queued</c> still say which moves next. A <b>count, not a clock</b>: no completion time
/// is offered, because how long a verb takes is not something this API has measured.</param>
public sealed record Job(
    string Id,
    string ServerId,
    string Verb,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SettledAt,
    string? Error,
    string? Phase = null,
    string? Blueprint = null,
    string? BatchId = null,
    int? QueuedPosition = null);

/// <summary>The <c>202 Accepted</c> body: <c>{ "job": { ... } }</c> (architecture.html §3).</summary>
public sealed record CommandAccepted(Job Job);

/// <summary>
/// The job execution lifecycle: <see cref="Queued"/> on accept, <see cref="Running"/> while the verb
/// executes, then a terminal <see cref="Succeeded"/>/<see cref="Failed"/> once it settles and the API
/// has re-checked authoritative run-state.
/// <para>
/// <see cref="Queued"/> is a real waiting state, not a formality. A batch creates every member's job
/// when it is accepted and lets the worker reach them at its own pace, so a job can sit here for as
/// long as the work ahead of it takes — which is what makes pending work visible on the server it is
/// going to happen to.
/// </para>
/// </summary>
public static class JobState
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    /// <summary>Cancelled before it ran. A job that never started has no honest outcome among the
    /// other two terminal states: it did not succeed, and calling it a failure would report a verb
    /// that was never attempted.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Is this a state the job will not leave? What releases the registry's per-server
    /// in-flight slot.</summary>
    public static bool IsTerminal(string state) => state is Succeeded or Failed or Cancelled;
}
