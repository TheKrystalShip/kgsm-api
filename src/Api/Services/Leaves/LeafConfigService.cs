using System.Globalization;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>The result of an apply request: either a validation error (→ controller 400 envelope) or the
/// applied/rolled-back/unchanged outcome.</summary>
public sealed record LeafConfigApplyResponse(LeafConfigApplyResult? Result, string? ErrorMessage)
{
    public static LeafConfigApplyResponse Ok(LeafConfigApplyResult r) => new(r, null);
    public static LeafConfigApplyResponse BadRequest(string message) => new(null, message);
}

/// <summary>
/// The leaf-runtime-config apply broker (Phase 2): builds the <see cref="LeafConfig"/> read view (manifest ⋈
/// overrides) and applies a <see cref="LeafConfigUpdate"/> with the <strong>write → render → restart →
/// health-canary → auto-rollback</strong> algorithm. Schema-agnostic: it only writes the manifest's
/// <c>KEY=value</c> overrides, never touches a leaf's own config.
/// </summary>
/// <remarks>
/// <b>Safe by construction.</b> A bad value can crash a leaf on restart, so every apply is a canary: after
/// the restart it polls <see cref="ILeafProbe"/> up to <see cref="ApiOptions.LeafApplyCanaryMs"/>; if the
/// leaf is not healthy in time it restores the pre-change overrides + restarts again (rollback) → the leaf
/// ends up healthy on its previous config and the API reports the rejection honestly.
/// <b>Secret hygiene:</b> a secret value is never echoed back (the read view masks it), never logged, and
/// only the changed key NAMES land in the audit meta.
/// </remarks>
public sealed class LeafConfigService(
    LeafOverrideStore store,
    LeafOverrideRenderer renderer,
    IUnitController unitController,
    ILeafProbe probe,
    AuditService audit,
    ApiOptions options,
    ILogger<LeafConfigService> logger)
{
    // Config changes are rare + globally serialized (a restart per apply) — one gate for the whole process.
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    /// <summary>Build the read view for a leaf (manifest ⋈ stored overrides), or null when it is not a config
    /// target.</summary>
    public async Task<LeafConfig?> GetConfigAsync(string leafId, CancellationToken ct)
    {
        if (!LeafConfigManifest.IsConfigTarget(leafId))
            return null;
        return await BuildConfigAsync(leafId, ct).ConfigureAwait(false);
    }

    /// <summary>Apply a config update with the canary/rollback algorithm. The caller has already 404'd a
    /// non-config-target leaf.</summary>
    public async Task<LeafConfigApplyResponse> ApplyAsync(
        string leafId, LeafConfigUpdate update, string? actor, string? origin, CancellationToken ct)
    {
        IReadOnlyList<LeafConfigFieldDef>? fields = LeafConfigManifest.For(leafId);
        if (fields is null)
            return LeafConfigApplyResponse.BadRequest($"'{leafId}' is not a configurable leaf");

        // --- validate + coerce (reject unknown keys / bad values BEFORE any write) ---
        var sets = new List<(LeafConfigFieldDef Field, string Value)>();
        var resetKeys = new HashSet<string>(update.Reset ?? [], StringComparer.Ordinal);

        if (update.Values is not null)
        {
            foreach ((string key, string raw) in update.Values)
            {
                LeafConfigFieldDef? field = LeafConfigManifest.Field(leafId, key);
                if (field is null)
                    return LeafConfigApplyResponse.BadRequest($"unknown config key '{key}'");
                if (resetKeys.Contains(key))
                    return LeafConfigApplyResponse.BadRequest($"key '{key}' is in both values and reset");
                if (!TryCoerce(field, raw, out string coerced, out string? err))
                    return LeafConfigApplyResponse.BadRequest(err!);
                sets.Add((field, coerced));
            }
        }
        foreach (string key in resetKeys)
        {
            if (LeafConfigManifest.Field(leafId, key) is null)
                return LeafConfigApplyResponse.BadRequest($"unknown config key '{key}'");
        }

        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<LeafOverrideRow> snapshot = await store.GetAsync(leafId, ct).ConfigureAwait(false);
            var target = snapshot.ToDictionary(r => r.Key, StringComparer.Ordinal);

            var changedKeys = new List<string>();
            foreach ((LeafConfigFieldDef field, string value) in sets)
            {
                bool isChange = !target.TryGetValue(field.Key, out LeafOverrideRow? cur)
                                || cur.Value != value || cur.IsSecret != field.IsSecret;
                target[field.Key] = new LeafOverrideRow(field.Key, value, field.IsSecret);
                if (isChange) changedKeys.Add(field.Key);
            }
            foreach (string key in resetKeys)
            {
                if (target.Remove(key)) changedKeys.Add(key);
            }

            LeafDescriptor leaf = LeafCatalog.Default.First(l => string.Equals(l.Id, leafId, StringComparison.Ordinal));

            if (changedKeys.Count == 0)
            {
                LeafConfig unchangedCfg = await BuildConfigAsync(leafId, ct).ConfigureAwait(false);
                LeafConfigHealth h = await SingleHealthAsync(leafId, ct).ConfigureAwait(false);
                return LeafConfigApplyResponse.Ok(new LeafConfigApplyResult(
                    LeafConfigOutcome.Unchanged, h, "No changes to apply.", unchangedCfg));
            }

            List<LeafOverrideRow> targetRows = target.Values.ToList();

            // --- apply: persist + render + restart ---
            await store.ReplaceAsync(leafId, targetRows, ct).ConfigureAwait(false);
            renderer.Render(leafId, targetRows);
            await unitController.RestartAsync(leaf.Unit, ct).ConfigureAwait(false);

            // --- canary ---
            bool healthy = await PollHealthyAsync(leafId, options.LeafApplyCanaryMs, ct).ConfigureAwait(false);
            if (healthy)
            {
                await AuditAsync(leaf, LeafConfigOutcome.Applied, changedKeys, actor, origin, AuditSeverity.Info, ct)
                    .ConfigureAwait(false);
                LeafConfig cfg = await BuildConfigAsync(leafId, ct).ConfigureAwait(false);
                return LeafConfigApplyResponse.Ok(new LeafConfigApplyResult(
                    LeafConfigOutcome.Applied,
                    new LeafConfigHealth(CapabilityStatus.Operational, null),
                    $"Applied {changedKeys.Count} change(s); {leaf.DisplayName} is healthy.",
                    cfg));
            }

            // --- rollback: restore the snapshot + restart again ---
            logger.LogWarning("config apply for {Leaf} failed its health canary; rolling back {Count} change(s)",
                leafId, changedKeys.Count);
            await store.ReplaceAsync(leafId, snapshot, ct).ConfigureAwait(false);
            renderer.Render(leafId, snapshot);
            await unitController.RestartAsync(leaf.Unit, ct).ConfigureAwait(false);
            bool postHealthy = await PollHealthyAsync(leafId, options.LeafApplyCanaryMs, ct).ConfigureAwait(false);

            await AuditAsync(leaf, LeafConfigOutcome.RolledBack, changedKeys, actor, origin, AuditSeverity.Warn, ct)
                .ConfigureAwait(false);
            LeafConfig rolledCfg = await BuildConfigAsync(leafId, ct).ConfigureAwait(false);
            int seconds = Math.Max(1, options.LeafApplyCanaryMs / 1000);
            return LeafConfigApplyResponse.Ok(new LeafConfigApplyResult(
                LeafConfigOutcome.RolledBack,
                new LeafConfigHealth(
                    postHealthy ? CapabilityStatus.Operational : CapabilityStatus.Down,
                    postHealthy ? null : $"{leaf.DisplayName} did not recover after rollback."),
                $"Change rejected — {leaf.DisplayName} failed its health check within {seconds}s; "
                    + "rolled back to the previous configuration.",
                rolledCfg));
        }
        finally { _applyGate.Release(); }
    }

    // Poll the canary until healthy or the window elapses (~500ms cadence).
    private async Task<bool> PollHealthyAsync(string leafId, int windowMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
        while (true)
        {
            if (await probe.IsHealthyAsync(leafId, ct).ConfigureAwait(false))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private async Task<LeafConfigHealth> SingleHealthAsync(string leafId, CancellationToken ct)
    {
        bool healthy = await probe.IsHealthyAsync(leafId, ct).ConfigureAwait(false);
        return new LeafConfigHealth(healthy ? CapabilityStatus.Operational : CapabilityStatus.Down, null);
    }

    private async Task<LeafConfig> BuildConfigAsync(string leafId, CancellationToken ct)
    {
        IReadOnlyList<LeafConfigFieldDef> fields = LeafConfigManifest.For(leafId)!;
        IReadOnlyList<LeafOverrideRow> rows = await store.GetAsync(leafId, ct).ConfigureAwait(false);
        var byKey = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
        LeafDescriptor leaf = LeafCatalog.Default.First(l => string.Equals(l.Id, leafId, StringComparison.Ordinal));

        var fieldDtos = new List<LeafConfigField>(fields.Count);
        foreach (LeafConfigFieldDef f in fields)
        {
            bool overridden = byKey.TryGetValue(f.Key, out LeafOverrideRow? row);
            if (f.IsSecret)
            {
                // Write-only: value ALWAYS null; surface set + an optional last-4 fingerprint (never the secret).
                fieldDtos.Add(new LeafConfigField(
                    f.Key, f.EnvName, f.Label, f.Description, f.Type, f.Enum,
                    IsSecret: true, Overridden: overridden, Value: null, Default: null,
                    Set: overridden, Fingerprint: overridden ? Fingerprint(row!.Value) : null));
            }
            else
            {
                fieldDtos.Add(new LeafConfigField(
                    f.Key, f.EnvName, f.Label, f.Description, f.Type, f.Enum,
                    IsSecret: false, Overridden: overridden,
                    Value: overridden ? row!.Value : null, Default: null));
            }
        }
        return new LeafConfig(leafId, leaf.DisplayName, leaf.Unit, fieldDtos);
    }

    private async Task AuditAsync(
        LeafDescriptor leaf, string outcome, IReadOnlyList<string> changedKeys,
        string? actor, string? origin, string severity, CancellationToken ct)
    {
        var meta = new Dictionary<string, string>
        {
            ["outcome"] = outcome,
            ["keys"] = string.Join(",", changedKeys), // KEY names only — never a value (secret hygiene)
        };
        string verb = outcome == LeafConfigOutcome.RolledBack ? "rejected config change for" : "configured";
        var write = new AuditWrite(
            Ts: DateTimeOffset.UtcNow,
            Origin: AuditMapping.NormalizeOrigin(origin),
            Actor: AuditMapping.ParseActor(actor),
            Action: AuditAction.ServiceConfig,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Leaf, leaf.Id, leaf.DisplayName),
            ServerId: null,
            HostId: options.HostId,
            Summary: $"{verb} {leaf.DisplayName} ({string.Join(", ", changedKeys)})",
            Meta: meta);
        await audit.AppendAsync(write, ct).ConfigureAwait(false);
    }

    // last-4 fingerprint, only when long enough that it reveals little — else null (never the whole secret).
    private static string? Fingerprint(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length >= 8 ? value[^4..] : null;

    // Coerce a string-encoded value to its canonical override form by the manifest field's type. Strips CR/LF
    // (a value can never span lines). An empty value is rejected — use `reset` to clear an override.
    private static bool TryCoerce(LeafConfigFieldDef field, string? raw, out string value, out string? error)
    {
        value = "";
        error = null;
        string v = (raw ?? "").Replace("\r", "").Replace("\n", "").Trim();
        if (v.Length == 0)
        {
            error = $"value for '{field.Key}' cannot be empty (use reset to clear an override)";
            return false;
        }

        switch (field.Type)
        {
            case LeafConfigFieldType.Int:
                if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                {
                    error = $"'{field.Key}' must be an integer";
                    return false;
                }
                value = n.ToString(CultureInfo.InvariantCulture);
                return true;

            case LeafConfigFieldType.Bool:
                switch (v.ToLowerInvariant())
                {
                    case "true" or "1" or "yes" or "on": value = "true"; return true;
                    case "false" or "0" or "no" or "off": value = "false"; return true;
                    default:
                        error = $"'{field.Key}' must be true or false";
                        return false;
                }

            case LeafConfigFieldType.Enum:
                string? match = field.Enum?.FirstOrDefault(e => string.Equals(e, v, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    error = $"'{field.Key}' must be one of: {string.Join(", ", field.Enum ?? [])}";
                    return false;
                }
                value = match; // canonical casing from the manifest
                return true;

            // string + secret: opaque, taken verbatim (already CR/LF-stripped + trimmed).
            default:
                value = v;
                return true;
        }
    }
}
