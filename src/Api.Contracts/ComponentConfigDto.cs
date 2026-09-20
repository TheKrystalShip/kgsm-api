using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One component's configuration surface — its settable-key manifest joined with the overrides in force.
/// </summary>
/// <remarks>
/// <para>
/// A COMPONENT, not a leaf. The same shape describes a service a node runs and an anchor that is a peer
/// of every node, because the panel renders both through one set of controls and a second shape would
/// mean a second renderer to keep in agreement with the first. What differs is who answers: a node
/// serves this for each of its leaves at <c>GET /hosts/{id}/services/{leaf}/config</c>, and an anchor
/// serves its own on its own origin.
/// </para>
/// <para>
/// Schema-agnostic. Whoever serves it knows the manifest's keys and writes <c>EnvName=value</c>
/// overrides; it never knows the component's own configuration schema.
/// </para>
/// </remarks>
/// <param name="Id">The component's stable short id — <c>monitor</c>, <c>assistant</c>, <c>auth-anchor</c>.
/// The same id its descriptor declares, so the wire and the file it is projected from agree.</param>
public sealed record ComponentConfigView(
    string Id,
    string DisplayName,
    string Unit,
    IReadOnlyList<ComponentConfigField> Fields,
    // Display sections, ascending by Order. Empty for a component whose surface renders flat.
    IReadOnlyList<ComponentConfigGroup> Groups,
    // Whether a PUT would be accepted. False when this host has not wired the component for config
    // delivery — the surface is still readable, so the panel shows the values and explains why they
    // are locked.
    bool Editable,
    // Why editing is unavailable; null when Editable.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditableReason,
    // How a change takes effect: restart (the process is bounced) or reload.
    string ApplyMode,
    // Whether this surface came from the component's own shipped descriptor. False means whoever is
    // serving it holds none and only the keys it already knew are exposed — the panel can say so
    // rather than implying the short list is the whole surface.
    bool FromDescriptor);

/// <summary>A display section on a leaf's config page.</summary>
public sealed record ComponentConfigGroup(string Id, string Label, int Order);

/// <summary>
/// One settable config field. <see cref="Key"/> is the stable id used in <see cref="ComponentConfigUpdate"/>;
/// <see cref="EnvName"/> is the env var the override writes (info only). Honesty: <see cref="Default"/> is
/// the leaf's coded default as its own descriptor declares it, and <see cref="Floor"/> what the host's
/// deploy files set — either is null when there is genuinely none, and <see cref="Source"/> says
/// <c>unknown</c> rather than picking a plausible tier. A secret's <see cref="Value"/> is
/// <strong>always null</strong> (write-only), surfaced instead as <see cref="Set"/> + an optional last-4
/// <see cref="Fingerprint"/>.
/// </summary>
public sealed record ComponentConfigField(
    string Key,
    string EnvName,
    string Label,
    string Description,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Enum,
    bool IsSecret,
    // Whether an override row exists for this key (vs. running on the deploy-floor).
    bool Overridden,
    // The current override value, or null when not overridden. ALWAYS null for a secret (never echoed).
    // Emitted even when null so the SPA binds one stable shape.
    string? Value,
    // The leaf's coded default from its descriptor. Null when it declares none, or when the leaf has shipped
    // no descriptor — never fabricated.
    string? Default,
    // Secret-only: whether this secret has a value at all — an override, or one the leaf's own deploy files
    // already carry — and a last-4 fingerprint of the override when there is one. Knowing a secret is set is
    // not knowing the secret, so this is reported while the value never is.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Set = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fingerprint = null,
    // The leaf's own configured value (its unit / env file / settings file), when this API could read the
    // sources the descriptor declares. Null when unset there, unreadable, or the field is a secret.
    string? Floor = null,
    // What the leaf is actually running with: override → floor → default. Null for a secret (never echoed)
    // and null when Source is "unknown".
    string? Effective = null,
    // Which tier Effective came from — a ComponentConfigSource value. "unknown" when a declared floor source could
    // not be read; that is never quietly downgraded to "default".
    string Source = ComponentConfigSource.Unknown,
    // Presentation + safety metadata from the descriptor.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Group = null,
    string Risk = ComponentConfigRisk.Safe,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PairedApiKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DependsOn = null);

/// <summary>
/// The <c>PUT /hosts/{id}/services/{leaf}/config</c> body. <see cref="Values"/> sets/replaces overrides
/// (string-encoded; coerced by the manifest field's type); <see cref="Reset"/> deletes overrides (reverts
/// those keys to the deploy-floor). An unknown key in either is a <c>400</c>.
/// </summary>
public sealed record ComponentConfigUpdate(
    IReadOnlyDictionary<string, string>? Values,
    IReadOnlyList<string>? Reset);

/// <summary>The post-apply leaf health (the canary verdict). <see cref="Status"/> is the capability
/// vocabulary subset <c>operational|down|unknown</c>; <see cref="Message"/> is an optional honest line.</summary>
public sealed record ComponentConfigHealth(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

/// <summary>
/// The outcome of a config apply (<c>PUT .../config</c>). <see cref="Outcome"/> is
/// <c>applied|rolled_back|unchanged</c>; <see cref="Config"/> is the refetched manifest+overrides so the SPA
/// re-renders without a second round-trip; <see cref="Message"/> is an honest human line.
/// </summary>
public sealed record ComponentConfigApplyResult(
    string Outcome,
    ComponentConfigHealth Health,
    string Message,
    ComponentConfigView Config);

/// <summary>The field-type vocabulary for a <see cref="ComponentConfigField"/>.</summary>
public static class ComponentConfigFieldType
{
    public const string String = "string";
    public const string Int = "int";
    public const string Bool = "bool";
    public const string Enum = "enum";
    public const string Secret = "secret";

    /// <summary>A filesystem path. Coerced like a string; the panel renders it as a path.</summary>
    public const string Path = "path";

    /// <summary>A comma-separated list, stored as the joined string the leaf parses.</summary>
    public const string Csv = "csv";

    /// <summary>An integer quantity of time; coerced like <see cref="Int"/> with the field's unit.</summary>
    public const string Duration = "duration";

    /// <summary>A number that may carry a fraction (a similarity threshold, a sampling temperature).
    /// Separate from <see cref="Int"/> because coercing one to an integer would silently destroy it.</summary>
    public const string Float = "float";

    public static readonly IReadOnlyList<string> All =
        [String, Int, Bool, Enum, Secret, Path, Csv, Duration, Float];
}

/// <summary>
/// How dangerous a field is to change. This never blocks an edit — every key is editable; it changes how the
/// panel presents one, and <see cref="Wiring"/> additionally triggers the post-apply reachability check.
/// </summary>
public static class ComponentConfigRisk
{
    /// <summary>The failure mode is the leaf doing its job differently.</summary>
    public const string Safe = "safe";

    /// <summary>Changing it can sever the link between this leaf and something else. The liveness canary
    /// cannot catch this: the leaf restarts perfectly, the API just cannot find it any more.</summary>
    public const string Wiring = "wiring";

    /// <summary>Changing it can drop data — a retention cutoff, a database path that orphans its store.</summary>
    public const string Destructive = "destructive";

    public static readonly IReadOnlyList<string> All = [Safe, Wiring, Destructive];
}

/// <summary>Which tier a field's effective value came from. Never guessed.</summary>
public static class ComponentConfigSource
{
    /// <summary>An override this API stores and renders.</summary>
    public const string Override = "override";

    /// <summary>The leaf's own configuration — its unit, env file or settings file.</summary>
    public const string Floor = "floor";

    /// <summary>The leaf's coded default, as recorded in its descriptor.</summary>
    public const string Default = "default";

    /// <summary>Genuinely unknown: a declared floor source could not be read, so this API cannot say what the
    /// leaf is running with. Distinct from — and never downgraded to — <see cref="Default"/>.</summary>
    public const string Unknown = "unknown";
}

/// <summary>The apply outcomes for a <see cref="ComponentConfigApplyResult"/>.</summary>
public static class ComponentConfigOutcome
{
    public const string Applied = "applied";
    public const string RolledBack = "rolled_back";
    public const string Unchanged = "unchanged";

    /// <summary>
    /// The change was applied and the leaf restarted cleanly, but this API can no longer reach it — the
    /// signature of a <see cref="ComponentConfigRisk.Wiring"/> change. Reported rather than auto-reverted: the
    /// change was asked for, and a silent revert would be a lie about what is running.
    /// </summary>
    public const string AppliedUnreachable = "applied_unreachable";

    /// <summary>
    /// The override was written and the restart that would put it in force was refused — by polkit, or by a
    /// systemd that would not take the job. The component is still running on the values it started with.
    /// <para>
    /// Distinct from <see cref="Applied"/> because it is the opposite claim about what is running, and a
    /// person reading "applied" would stop looking. A component serving its own surface is where this
    /// arises: it queues its own restart and cannot watch the result, so whether the job was accepted is
    /// the whole of what it can honestly report.
    /// </para>
    /// </summary>
    public const string WrittenNotApplied = "written_not_applied";
}
