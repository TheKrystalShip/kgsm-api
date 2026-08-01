using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// One settable config field's static definition, before joining overrides. Produced from a leaf's shipped
/// config descriptor where it has one, and from <see cref="LeafConfigManifest"/> where it does not.
/// </summary>
/// <param name="Key">The stable id used on the wire + in a PUT (e.g. <c>logLevel</c>).</param>
/// <param name="EnvName">The env var the override file writes (<c>Logging__LogLevel__Default</c>).</param>
/// <param name="Type">A <see cref="LeafConfigFieldType"/> value.</param>
/// <param name="Enum">The allowed values when <see cref="Type"/> is <c>enum</c>; else null.</param>
public sealed record LeafConfigFieldDef(
    string Key,
    string EnvName,
    string Label,
    string Description,
    string Type,
    IReadOnlyList<string>? Enum = null)
{
    /// <summary>A secret (write-only) field — masked on read, never logged.</summary>
    public bool IsSecret => Type == LeafConfigFieldType.Secret;

    /// <summary>The display section this field belongs to (a <see cref="LeafConfigGroup"/> id), or null.</summary>
    public string? Group { get; init; }

    /// <summary>The leaf's own coded default, as a string. Rendered as the lowest provenance tier and
    /// <strong>never</strong> written as an override. Null when the leaf has no default for this key.</summary>
    public string? Default { get; init; }

    /// <summary>Inclusive bounds for a numeric field, mirroring the leaf's own parser floor so a value it
    /// would silently discard is rejected here instead — before any restart.</summary>
    public long? Min { get; init; }

    /// <inheritdoc cref="Min"/>
    public long? Max { get; init; }

    /// <summary>Display suffix (<c>ms</c>, <c>days</c>, <c>MB</c>). Presentation only.</summary>
    public string? Unit { get; init; }

    /// <summary>A <see cref="LeafConfigRisk"/> value. Never blocks an edit — it changes how the panel
    /// presents one, and <c>wiring</c> additionally triggers the post-apply reachability check.</summary>
    public string Risk { get; init; } = LeafConfigRisk.Safe;

    /// <summary>The kgsm-api setting that has to agree with this one (e.g. this leaf's socket path and the
    /// API's view of it). Checked after an apply; a disagreement is reported, never silently tolerated.</summary>
    public string? PairedApiKey { get; init; }

    /// <summary>Another field's key that must be set for this one to have any effect. Presentation only —
    /// the API does not enforce it, because the leaf is the authority on its own semantics.</summary>
    public string? DependsOn { get; init; }
}

/// <summary>
/// The built-in fallback config surface, for a leaf that has not shipped its own config descriptor yet.
/// <see cref="LeafConfigCatalog"/> prefers the descriptor wherever one exists; this table keeps a leaf
/// configurable in the meantime rather than making the rollout a flag day.
/// </summary>
/// <remarks>
/// <strong>Every key here is a confirmed-real env var</strong> the leaf actually reads (never a fabricated
/// one): <c>logLevel</c> rides the ecosystem-standard <c>Logging__LogLevel__Default</c> on every .NET leaf;
/// the rest were verified against each leaf's own config (kgsm-monitor <c>KGSM_MONITOR_INTERVAL_MS</c>,
/// kgsm-watchdog <c>KGSM_WATCHDOG_POLL_INTERVAL_MS</c>, the assistant's <c>Rag__Enabled</c> +
/// <c>WebSearch__ApiKey</c>). The display name + unit come from <see cref="LeafCatalog"/>. These entries
/// carry no default, bounds or risk — a leaf's own descriptor is where that detail comes from.
/// </remarks>
public static class LeafConfigManifest
{
    /// <summary>The Microsoft.Extensions.Logging level enum (the <c>Logging__LogLevel__Default</c> values).</summary>
    public static readonly IReadOnlyList<string> LogLevels =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    // The standard .NET logging-level override — real on every leaf (the ecosystem logging convention).
    private static LeafConfigFieldDef LogLevelField() => new(
        Key: "logLevel",
        EnvName: "Logging__LogLevel__Default",
        Label: "Log level",
        Description: "Minimum severity this leaf logs (the standard .NET logging-level override).",
        Type: LeafConfigFieldType.Enum,
        Enum: LogLevels);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<LeafConfigFieldDef>> ByLeaf =
        new Dictionary<string, IReadOnlyList<LeafConfigFieldDef>>(StringComparer.Ordinal)
        {
            [ProvisionableLeaf.Monitor] =
            [
                LogLevelField(),
                new("intervalMs", "KGSM_MONITOR_INTERVAL_MS", "Sample interval (ms)",
                    "How often the monitor samples host & per-server metrics, in milliseconds.",
                    LeafConfigFieldType.Int),
            ],
            [ProvisionableLeaf.Watchdog] =
            [
                LogLevelField(),
                new("pollIntervalMs", "KGSM_WATCHDOG_POLL_INTERVAL_MS", "Supervision poll interval (ms)",
                    "How often the watchdog reconciles its supervised instances, in milliseconds.",
                    LeafConfigFieldType.Int),
            ],
            [ProvisionableLeaf.Assistant] =
            [
                LogLevelField(),
                new("ragEnabled", "Rag__Enabled", "Knowledge base (RAG)",
                    "Enable retrieval-augmented context so the assistant can search its knowledge base.",
                    LeafConfigFieldType.Bool),
                new("webSearchApiKey", "WebSearch__ApiKey", "Web search API key",
                    "Tavily API key enabling the assistant's web-search tool. Write-only — never shown again.",
                    LeafConfigFieldType.Secret),
            ],
            // Firewall: log level only — no other env key on kgsm-firewall is confirmed safe to expose yet.
            [ProvisionableLeaf.Firewall] =
            [
                LogLevelField(),
            ],
            [ProvisionableLeaf.Scheduler] =
            [
                LogLevelField(),
            ],
        };

    /// <summary>True when <paramref name="leafId"/> is a config target (has a manifest).</summary>
    public static bool IsConfigTarget(string? leafId) => leafId is not null && ByLeaf.ContainsKey(leafId);

    /// <summary>The manifest fields for a leaf, or null when it is not a config target.</summary>
    public static IReadOnlyList<LeafConfigFieldDef>? For(string leafId) =>
        ByLeaf.TryGetValue(leafId, out IReadOnlyList<LeafConfigFieldDef>? fields) ? fields : null;

    /// <summary>Look up one field by key within a leaf's manifest (null when unknown).</summary>
    public static LeafConfigFieldDef? Field(string leafId, string key) =>
        For(leafId)?.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}
