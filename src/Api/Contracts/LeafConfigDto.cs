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
    IReadOnlyList<LeafConfigField> Fields);

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
    // The deploy-floor value IF the API can honestly know it (it can't today → null, never fabricated).
    string? Default,
    // Secret-only: whether a secret override is currently set, and an optional last-4 fingerprint (debug).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Set = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fingerprint = null);

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
}

/// <summary>The apply outcomes for a <see cref="LeafConfigApplyResult"/>.</summary>
public static class LeafConfigOutcome
{
    public const string Applied = "applied";
    public const string RolledBack = "rolled_back";
    public const string Unchanged = "unchanged";
}
