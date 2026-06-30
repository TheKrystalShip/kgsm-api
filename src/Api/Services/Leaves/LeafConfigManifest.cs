using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>One settable config field's static descriptor (the manifest entry, before joining overrides).</summary>
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
}

/// <summary>
/// The per-leaf settable-key manifest (the leaf-runtime-config feature) — the ONLY keys the API will expose
/// and write as overrides. <strong>Every key here is a confirmed-real env var</strong> the leaf actually
/// reads (never a fabricated one): <c>logLevel</c> rides the ecosystem-standard
/// <c>Logging__LogLevel__Default</c> on every .NET leaf; the rest were verified against each leaf's own
/// config (kgsm-monitor <c>KGSM_MONITOR_INTERVAL_MS</c>, kgsm-watchdog <c>KGSM_WATCHDOG_POLL_INTERVAL_MS</c>,
/// the assistant's <c>Rag__Enabled</c> + <c>WebSearch__ApiKey</c>). The display name + unit come from
/// <see cref="LeafCatalog"/>.
/// </summary>
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
