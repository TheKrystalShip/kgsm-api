using System.Globalization;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>The result of an apply request: either a validation error (→ controller 400 envelope) or the
/// applied/rolled-back/unchanged outcome.</summary>
public sealed record LeafConfigApplyResponse(LeafConfigApplyResult? Result, string? ErrorMessage, bool IsConflict = false)
{
    public static LeafConfigApplyResponse Ok(LeafConfigApplyResult r) => new(r, null);
    public static LeafConfigApplyResponse BadRequest(string message) => new(null, message);

    /// <summary>The request is well-formed but this host cannot deliver it (the leaf has no override
    /// drop-in) — a 409, not a 400: nothing about the body is wrong.</summary>
    public static LeafConfigApplyResponse Conflict(string message) => new(null, message, IsConflict: true);
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
    LeafConfigCatalog catalog,
    LeafFloorReader floorReader,
    IUnitController unitController,
    ILeafProbe probe,
    ILeafReachability reachability,
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
        if (!catalog.IsConfigTarget(leafId))
            return null;
        return await BuildConfigAsync(leafId, ct).ConfigureAwait(false);
    }

    /// <summary>Apply a config update with the canary/rollback algorithm. The caller has already 404'd a
    /// non-config-target leaf.</summary>
    public async Task<LeafConfigApplyResponse> ApplyAsync(
        string leafId, LeafConfigUpdate update, string? actor, string? origin, CancellationToken ct)
    {
        LeafConfigIdentity? identity = catalog.Identity(leafId);
        if (identity is null || catalog.For(leafId) is null)
            return LeafConfigApplyResponse.BadRequest($"'{leafId}' is not a configurable leaf");

        // This host must actually be able to deliver the change. Without the override drop-in the write
        // would render a file nothing reads and then fail at the restart — refuse up front, with the fix.
        if (!catalog.IsEditable(leafId, out string? lockedReason))
            return LeafConfigApplyResponse.Conflict(lockedReason!);

        // --- validate + coerce (reject unknown keys / bad values BEFORE any write) ---
        var sets = new List<(LeafConfigFieldDef Field, string Value)>();
        var resetKeys = new HashSet<string>(update.Reset ?? [], StringComparer.Ordinal);

        if (update.Values is not null)
        {
            foreach ((string key, string raw) in update.Values)
            {
                LeafConfigFieldDef? field = catalog.Field(leafId, key);
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
            if (catalog.Field(leafId, key) is null)
                return LeafConfigApplyResponse.BadRequest($"unknown config key '{key}'");
        }

        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<LeafOverrideRow> snapshot = await store.GetAsync(leafId, ct).ConfigureAwait(false);
            var target = snapshot.ToDictionary(r => r.Key, StringComparer.Ordinal);

            var changedKeys = new List<string>();
            var changedFields = new List<LeafConfigFieldDef>();
            foreach ((LeafConfigFieldDef field, string value) in sets)
            {
                bool isChange = !target.TryGetValue(field.Key, out LeafOverrideRow? cur)
                                || cur.Value != value || cur.IsSecret != field.IsSecret;
                target[field.Key] = new LeafOverrideRow(field.Key, value, field.IsSecret);
                if (isChange) { changedKeys.Add(field.Key); changedFields.Add(field); }
            }
            foreach (string key in resetKeys)
            {
                if (!target.Remove(key)) continue;
                changedKeys.Add(key);
                if (catalog.Field(leafId, key) is { } resetField) changedFields.Add(resetField);
            }

            LeafConfigIdentity leaf = identity;

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
                // The unit is up. A wiring change can still have severed this API's link to it — the canary
                // cannot see that, so check it explicitly and report honestly rather than claiming success.
                string? severed = await CheckReachabilityAsync(leafId, changedFields, ct).ConfigureAwait(false);
                string outcome = severed is null ? LeafConfigOutcome.Applied : LeafConfigOutcome.AppliedUnreachable;

                await AuditAsync(leaf, outcome, changedKeys, actor, origin,
                        severed is null ? AuditSeverity.Info : AuditSeverity.Warn, ct)
                    .ConfigureAwait(false);
                LeafConfig cfg = await BuildConfigAsync(leafId, ct).ConfigureAwait(false);

                if (severed is null)
                {
                    return LeafConfigApplyResponse.Ok(new LeafConfigApplyResult(
                        LeafConfigOutcome.Applied,
                        new LeafConfigHealth(CapabilityStatus.Operational, null),
                        $"Applied {changedKeys.Count} change(s); {leaf.DisplayName} is healthy.",
                        cfg));
                }

                // Deliberately NOT auto-reverted: the change was asked for, and silently undoing it would
                // misreport what is running. Reset stays available and needs nothing from the leaf.
                logger.LogWarning("config applied to {Leaf} but it is no longer reachable from this API", leafId);
                return LeafConfigApplyResponse.Ok(new LeafConfigApplyResult(
                    LeafConfigOutcome.AppliedUnreachable,
                    new LeafConfigHealth(CapabilityStatus.Down, severed),
                    $"Applied {changedKeys.Count} change(s) and {leaf.DisplayName} restarted cleanly, but this "
                        + $"API can no longer reach it. {severed} Resetting restores the previous "
                        + "configuration and works even while the leaf is unreachable.",
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

    /// <summary>
    /// After a successful apply, whether a <see cref="LeafConfigRisk.Wiring"/> change severed this API's link
    /// to the leaf. Returns null when nothing is wrong OR when there is no signal — an absence of evidence is
    /// never reported as a break.
    /// </summary>
    private async Task<string?> CheckReachabilityAsync(
        string leafId, IReadOnlyList<LeafConfigFieldDef> changed, CancellationToken ct)
    {
        List<LeafConfigFieldDef> wiring =
            [.. changed.Where(f => string.Equals(f.Risk, LeafConfigRisk.Wiring, StringComparison.Ordinal))];
        if (wiring.Count == 0)
            return null;

        // A paired key is the deterministic half: the leaf now says one thing and this API's own setting says
        // another, and this API cannot change its own configuration here. Name both sides.
        foreach (LeafConfigFieldDef field in wiring.Where(f => f.PairedApiKey is not null))
        {
            LeafConfigField? current = (await BuildConfigAsync(leafId, ct).ConfigureAwait(false))
                .Fields.FirstOrDefault(x => string.Equals(x.Key, field.Key, StringComparison.Ordinal));
            string? leafValue = current?.Effective;
            string? apiValue = options.ResolvedByEnvName(field.PairedApiKey!);

            if (leafValue is null || apiValue is null)
                continue;   // cannot compare — say nothing rather than guess

            if (!string.Equals(leafValue, apiValue, StringComparison.Ordinal))
            {
                return $"{field.Label} is now '{leafValue}', but this API reads "
                     + $"{field.PairedApiKey}='{apiValue}'. To keep the change instead, set "
                     + $"{field.PairedApiKey} to match and restart the Control Panel API.";
            }
        }

        // The observed half: ask the capability model whether the leaf is still answering. A leaf that has
        // just restarted needs a moment before its health endpoint serves, so poll for the same window the
        // liveness canary uses — otherwise every wiring change reports a break that is really a race.
        bool? reachable = await PollReachableAsync(leafId, options.LeafApplyCanaryMs, ct).ConfigureAwait(false);
        return reachable == false
            ? "Its capability probe no longer answers."
            : null;   // true, or null (no probe for this leaf) — both mean "no break observed"
    }

    // Reachable as soon as it answers; unreachable only if it never does within the window. Null (no probe
    // for this leaf) short-circuits — there is nothing to wait for.
    private async Task<bool?> PollReachableAsync(string leafId, int windowMs, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
        while (true)
        {
            bool? reachable = await reachability.IsReachableAsync(leafId, ct).ConfigureAwait(false);
            if (reachable is null or true)
                return reachable;
            if (DateTime.UtcNow >= deadline)
                return false;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private async Task<LeafConfig> BuildConfigAsync(string leafId, CancellationToken ct)
    {
        IReadOnlyList<LeafConfigFieldDef> fields = catalog.For(leafId)!;
        LeafConfigIdentity identity = catalog.Identity(leafId)!;
        IReadOnlyList<LeafOverrideRow> rows = await store.GetAsync(leafId, ct).ConfigureAwait(false);
        var byKey = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);

        // The leaf's own configuration, so each field can report where its live value actually comes from.
        // Only a descriptor declares where that lives; without one the floor is genuinely unknown.
        LeafFloor floor = identity.Descriptor is null ? LeafFloor.Unknown : floorReader.Read(identity.Descriptor);

        bool editable = catalog.IsEditable(leafId, out string? reason);

        var fieldDtos = new List<LeafConfigField>(fields.Count);
        foreach (LeafConfigFieldDef f in fields)
        {
            bool overridden = byKey.TryGetValue(f.Key, out LeafOverrideRow? row);
            bool floorHas = floor.Values.TryGetValue(f.EnvName, out string? floorValue);

            // override → floor → default, and honest 'unknown' when a declared floor source was unreadable
            // (an unreadable floor could be setting this key; falling through to the default would invent it).
            string source =
                overridden ? LeafConfigSource.Override
                : floorHas ? LeafConfigSource.Floor
                : !floor.Complete ? LeafConfigSource.Unknown
                : f.Default is not null ? LeafConfigSource.Default
                : LeafConfigSource.Unknown;

            string? effective = source switch
            {
                LeafConfigSource.Override => row!.Value,
                LeafConfigSource.Floor => floorValue,
                LeafConfigSource.Default => f.Default,
                _ => null,
            };

            if (f.IsSecret)
            {
                // Write-only. Never echo the value — including from the floor, where the real secret lives.
                // The provenance tier is still reported: knowing a secret is set is not knowing the secret.
                fieldDtos.Add(new LeafConfigField(
                    f.Key, f.EnvName, f.Label, f.Description, f.Type, f.Enum,
                    IsSecret: true, Overridden: overridden, Value: null, Default: null,
                    Set: overridden || floorHas, Fingerprint: overridden ? Fingerprint(row!.Value) : null,
                    Floor: null, Effective: null, Source: source,
                    Group: f.Group, Risk: f.Risk, Unit: f.Unit, Min: f.Min, Max: f.Max,
                    PairedApiKey: f.PairedApiKey, DependsOn: f.DependsOn));
            }
            else
            {
                fieldDtos.Add(new LeafConfigField(
                    f.Key, f.EnvName, f.Label, f.Description, f.Type, f.Enum,
                    IsSecret: false, Overridden: overridden,
                    Value: overridden ? row!.Value : null, Default: f.Default,
                    Set: null, Fingerprint: null,
                    Floor: floorHas ? floorValue : null, Effective: effective, Source: source,
                    Group: f.Group, Risk: f.Risk, Unit: f.Unit, Min: f.Min, Max: f.Max,
                    PairedApiKey: f.PairedApiKey, DependsOn: f.DependsOn));
            }
        }

        var groups = identity.Groups
            .OrderBy(g => g.Order)
            .Select(g => new LeafConfigGroupDto(g.Id, g.Label, g.Order))
            .ToList();

        return new LeafConfig(
            leafId, identity.DisplayName, identity.Unit, fieldDtos, groups,
            Editable: editable, EditableReason: editable ? null : reason,
            ApplyMode: identity.ApplyMode, FromDescriptor: identity.FromDescriptor);
    }

    private async Task AuditAsync(
        LeafConfigIdentity leaf, string outcome, IReadOnlyList<string> changedKeys,
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
            case LeafConfigFieldType.Duration:
                if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                {
                    error = $"'{field.Key}' must be an integer";
                    return false;
                }
                // Enforce the leaf's own floor here rather than letting it silently discard the value: the
                // panel would otherwise report a change the leaf quietly ignored.
                if (!WithinBounds(field, n, out error))
                    return false;
                value = n.ToString(CultureInfo.InvariantCulture);
                return true;

            case LeafConfigFieldType.Float:
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    || double.IsNaN(d) || double.IsInfinity(d))
                {
                    error = $"'{field.Key}' must be a number";
                    return false;
                }
                if (!WithinBounds(field, d, out error))
                    return false;
                // Round-tripped so the leaf reads back exactly what was accepted, in the invariant format
                // its own parser expects — a machine with a comma decimal separator must not change this.
                value = d.ToString("R", CultureInfo.InvariantCulture);
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

            // string, secret, path, csv: opaque to the API, taken verbatim (already CR/LF-stripped +
            // trimmed). The leaf is the authority on what a valid path or list member is; second-guessing
            // it here would reject values the leaf accepts.
            default:
                value = v;
                return true;
        }
    }

    // The leaf's own declared floor/ceiling, enforced here rather than letting the leaf silently discard an
    // out-of-range value — the panel would otherwise report a change the leaf quietly ignored.
    private static bool WithinBounds(LeafConfigFieldDef field, double n, out string? error)
    {
        error = null;
        if (field.Min is { } min && n < min)
            error = $"'{field.Key}' must be at least {Number(min)}{Suffix(field)}";
        else if (field.Max is { } max && n > max)
            error = $"'{field.Key}' must be at most {Number(max)}{Suffix(field)}";
        return error is null;
    }

    /// <summary>A bound as an operator would write it: no trailing <c>.0</c> on a whole number.</summary>
    private static string Number(double v) => v.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Suffix(LeafConfigFieldDef field) =>
        field.Unit is null ? "" : " " + field.Unit;
}
