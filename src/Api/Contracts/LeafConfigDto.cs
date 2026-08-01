using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One configurable leaf's config surface (the leaf-runtime-config feature) — the per-leaf settable-key
/// manifest joined with the currently-stored overrides. Returned by <c>GET /hosts/{id}/services/{leaf}/config</c>
/// and embedded in <see cref="LeafConfigApplyResult.Config"/>. Schema-agnostic: the API only ever knows the
/// manifest's keys (it writes <c>EnvName=value</c> overrides), never the leaf's own config schema.
/// </summary>
public sealed record LeafConfig(
    string Leaf,
    string DisplayName,
    string Unit,
    IReadOnlyList<LeafConfigField> Fields,
    // Display sections, ascending by Order. Empty for a leaf whose surface renders flat.
    IReadOnlyList<LeafConfigGroupDto> Groups,
    // Whether a PUT would be accepted. False when this host has not wired the leaf for config delivery —
    // the surface is still readable, so the panel shows the values and explains why they are locked.
    bool Editable,
    // Why editing is unavailable; null when Editable.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditableReason,
    // How a change takes effect: restart (the leaf is bounced) or reload.
    string ApplyMode,
    // Whether this leaf's surface came from its own shipped descriptor. False means the leaf has not shipped
    // one yet and only the keys this API historically knew are exposed — the panel can say so rather than
    // implying the short list is the whole surface.
    bool FromDescriptor);

/// <summary>A display section on a leaf's config page.</summary>
public sealed record LeafConfigGroupDto(string Id, string Label, int Order);

/// <summary>
/// One settable config field. <see cref="Key"/> is the stable id used in <see cref="LeafConfigUpdate"/>;
/// <see cref="EnvName"/> is the env var the override writes (info only). Honesty: <see cref="Default"/> (the
/// deploy-floor value) is null when the API can't know it (it never reads the leaf's own files — never
/// fabricated); a secret's <see cref="Value"/> is <strong>always null</strong> (write-only), surfaced instead
/// as <see cref="Set"/> + an optional last-4 <see cref="Fingerprint"/>.
/// </summary>
public sealed record LeafConfigField(
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
    // Secret-only: whether a secret override is currently set, and an optional last-4 fingerprint (debug).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Set = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fingerprint = null,
    // The leaf's own configured value (its unit / env file / settings file), when this API could read the
    // sources the descriptor declares. Null when unset there, unreadable, or the field is a secret.
    string? Floor = null,
    // What the leaf is actually running with: override → floor → default. Null for a secret (never echoed)
    // and null when Source is "unknown".
    string? Effective = null,
    // Which tier Effective came from — a LeafConfigSource value. "unknown" when a declared floor source could
    // not be read; that is never quietly downgraded to "default".
    string Source = LeafConfigSource.Unknown,
    // Presentation + safety metadata from the descriptor.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Group = null,
    string Risk = LeafConfigRisk.Safe,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PairedApiKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DependsOn = null);

/// <summary>
/// The <c>PUT /hosts/{id}/services/{leaf}/config</c> body. <see cref="Values"/> sets/replaces overrides
/// (string-encoded; coerced by the manifest field's type); <see cref="Reset"/> deletes overrides (reverts
/// those keys to the deploy-floor). An unknown key in either is a <c>400</c>.
/// </summary>
public sealed record LeafConfigUpdate(
    IReadOnlyDictionary<string, string>? Values,
    IReadOnlyList<string>? Reset);

/// <summary>The post-apply leaf health (the canary verdict). <see cref="Status"/> is the capability
/// vocabulary subset <c>operational|down|unknown</c>; <see cref="Message"/> is an optional honest line.</summary>
public sealed record LeafConfigHealth(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

/// <summary>
/// The outcome of a config apply (<c>PUT .../config</c>). <see cref="Outcome"/> is
/// <c>applied|rolled_back|unchanged</c>; <see cref="Config"/> is the refetched manifest+overrides so the SPA
/// re-renders without a second round-trip; <see cref="Message"/> is an honest human line.
/// </summary>
public sealed record LeafConfigApplyResult(
    string Outcome,
    LeafConfigHealth Health,
    string Message,
    LeafConfig Config);

/// <summary>The field-type vocabulary for a <see cref="LeafConfigField"/>.</summary>
public static class LeafConfigFieldType
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

    public static readonly IReadOnlyList<string> All =
        [String, Int, Bool, Enum, Secret, Path, Csv, Duration];
}

/// <summary>
/// How dangerous a field is to change. This never blocks an edit — every key is editable; it changes how the
/// panel presents one, and <see cref="Wiring"/> additionally triggers the post-apply reachability check.
/// </summary>
public static class LeafConfigRisk
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
public static class LeafConfigSource
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

/// <summary>The apply outcomes for a <see cref="LeafConfigApplyResult"/>.</summary>
public static class LeafConfigOutcome
{
    public const string Applied = "applied";
    public const string RolledBack = "rolled_back";
    public const string Unchanged = "unchanged";

    /// <summary>
    /// The change was applied and the leaf restarted cleanly, but this API can no longer reach it — the
    /// signature of a <see cref="LeafConfigRisk.Wiring"/> change. Reported rather than auto-reverted: the
    /// change was asked for, and a silent revert would be a lie about what is running.
    /// </summary>
    public const string AppliedUnreachable = "applied_unreachable";
}
