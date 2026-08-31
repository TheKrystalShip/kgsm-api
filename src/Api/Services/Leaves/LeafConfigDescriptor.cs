using System.Text.Json;
using System.Text.Json.Serialization;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>Where one tier of a leaf's own (non-API) configuration lives — see <see cref="LeafFloorReader"/>.</summary>
/// <param name="Kind">
/// <c>systemd-unit</c> (the unit's <c>Environment=</c> assignments, drop-ins included) ·
/// <c>env-file</c> (a systemd <c>EnvironmentFile=</c> target) · <c>appsettings</c> (JSON flattened to
/// <c>Section__Key</c>, matching how <c>IConfiguration</c> maps env vars).
/// </param>
public sealed record LeafFloorSource(string Kind, string Path);

/// <summary>A display section on the leaf's config page. <see cref="Order"/> is ascending.</summary>
public sealed record LeafConfigGroup(string Id, string Label, int Order);

/// <summary>
/// A leaf's own declaration of its full configurable surface, shipped as
/// <c>/var/lib/kgsm/leaves/&lt;id&gt;.json</c> by that leaf's <c>deploy.sh</c> and read (never written) here.
/// The format is owned by <c>tks/leaf-config-descriptor.md</c>.
/// </summary>
/// <remarks>
/// Distinct from <see cref="LeafDescriptor"/>, which is <em>this API's</em> static catalog of what services a
/// host comprises. A config descriptor is the leaf's own statement about its knobs, and it can introduce a
/// leaf the catalog has never heard of.
/// </remarks>
public sealed record LeafConfigDescriptor(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Unit,
    string Role,
    bool OnDemand,
    string ApplyMode,
    IReadOnlyList<LeafFloorSource> FloorSources,
    IReadOnlyList<LeafConfigGroup> Groups,
    IReadOnlyList<LeafConfigFieldDef> Fields,
    /// <summary>The leaf declaring that its configuration can be read here but not changed here — the one
    /// case being this API itself, which cannot restart itself to apply a change without killing the
    /// request that asked for it. Distinct from an unwired host: no amount of provisioning changes it.</summary>
    bool ReadOnly = false,
    /// <summary>Why, in the leaf's own words. Shown instead of the "run setup" advice, which would be
    /// misleading here — there is nothing to run.</summary>
    string? ReadOnlyReason = null)
{
    /// <summary>The only schema version this API understands. A descriptor declaring anything else is skipped
    /// rather than guessed at — the format's forward-compatibility contract.</summary>
    public const int SupportedSchemaVersion = 1;

    public LeafConfigFieldDef? Field(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>
/// Parses and validates a descriptor file. Every failure is a <em>rejection with a reason</em>, never a
/// partially-populated descriptor: a half-understood config surface would let the panel offer a key the leaf
/// does not read, which reports "applied" while changing nothing.
/// </summary>
public static class LeafConfigDescriptorParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] Kinds = ["systemd-unit", "env-file", "appsettings"];
    private static readonly string[] ApplyModes = ["restart", "reload"];

    /// <summary>Parse <paramref name="json"/>. Returns null and sets <paramref name="error"/> on any problem.</summary>
    public static LeafConfigDescriptor? TryParse(string json, out string? error)
    {
        error = null;
        RawDescriptor? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawDescriptor>(json, Json);
        }
        catch (JsonException ex)
        {
            error = $"not valid JSON: {ex.Message}";
            return null;
        }

        if (raw is null)
        {
            error = "empty document";
            return null;
        }

        // Version first: an unknown version means the rest of this file may mean something else entirely.
        if (raw.SchemaVersion != LeafConfigDescriptor.SupportedSchemaVersion)
        {
            error = $"schemaVersion {raw.SchemaVersion} is not supported "
                  + $"(this API understands {LeafConfigDescriptor.SupportedSchemaVersion})";
            return null;
        }

        if (!Required(raw.Id, "id", ref error) ||
            !Required(raw.DisplayName, "displayName", ref error) ||
            !Required(raw.Unit, "unit", ref error) ||
            !Required(raw.Role, "role", ref error))
            return null;

        string applyMode = raw.ApplyMode ?? "restart";
        if (!ApplyModes.Contains(applyMode, StringComparer.Ordinal))
        {
            error = $"unknown applyMode '{applyMode}'";
            return null;
        }

        var floors = new List<LeafFloorSource>();
        foreach (RawFloorSource f in raw.FloorSources ?? [])
        {
            if (string.IsNullOrWhiteSpace(f.Kind) || !Kinds.Contains(f.Kind, StringComparer.Ordinal))
            {
                error = $"floorSources: unknown kind '{f.Kind}'";
                return null;
            }
            // A systemd-unit source names the UNIT, because where its file sits is a property of how
            // the host was provisioned — /usr/lib for a packaged unit, /etc for a deployed one — and a
            // leaf cannot know which. An absolute path is still accepted there, for a leaf whose unit
            // lives somewhere systemd's own roots do not cover. Every other kind is a real file, so
            // naming it any other way than absolutely is a descriptor that cannot be resolved.
            string path = f.Path ?? "";
            bool namesAUnit = string.Equals(f.Kind, "systemd-unit", StringComparison.Ordinal)
                              && path.EndsWith(".service", StringComparison.Ordinal);

            if (!namesAUnit && !path.StartsWith('/'))
            {
                error = "floorSources: path must be absolute";
                return null;
            }
            floors.Add(new LeafFloorSource(f.Kind, path));
        }

        var groups = new List<LeafConfigGroup>();
        foreach (RawGroup g in raw.Groups ?? [])
        {
            if (string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Label))
            {
                error = "groups: each group needs an id and a label";
                return null;
            }
            groups.Add(new LeafConfigGroup(g.Id, g.Label, g.Order));
        }

        if (raw.Fields is null || raw.Fields.Count == 0)
        {
            error = "no fields declared";
            return null;
        }

        var fields = new List<LeafConfigFieldDef>(raw.Fields.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);

        foreach (RawField f in raw.Fields)
        {
            if (!Required(f.Key, "a field's key", ref error) ||
                !Required(f.Env, $"field '{f.Key}' env", ref error) ||
                !Required(f.Label, $"field '{f.Key}' label", ref error) ||
                !Required(f.Description, $"field '{f.Key}' description", ref error))
                return null;

            if (!seenKeys.Add(f.Key!))
            {
                error = $"duplicate field key '{f.Key}'";
                return null;
            }

            string type = f.Type ?? "";
            if (!LeafConfigFieldType.All.Contains(type, StringComparer.Ordinal))
            {
                error = $"field '{f.Key}': unknown type '{type}'";
                return null;
            }

            string risk = f.Risk ?? LeafConfigRisk.Safe;
            if (!LeafConfigRisk.All.Contains(risk, StringComparer.Ordinal))
            {
                error = $"field '{f.Key}': unknown risk '{risk}'";
                return null;
            }

            if (type == LeafConfigFieldType.Enum && (f.Values is null || f.Values.Count == 0))
            {
                error = $"field '{f.Key}': an enum needs values";
                return null;
            }

            if (f.Group is not null && !groupIds.Contains(f.Group))
            {
                error = $"field '{f.Key}': references undefined group '{f.Group}'";
                return null;
            }

            fields.Add(new LeafConfigFieldDef(f.Key!, f.Env!, f.Label!, f.Description!, type, f.Values)
            {
                Group = f.Group,
                Default = f.Default,
                Min = f.Min,
                Max = f.Max,
                Unit = f.Unit,
                Risk = risk,
                PairedApiKey = f.PairedApiKey,
                DependsOn = f.DependsOn,
            });
        }

        // dependsOn is resolved last, once every key in the file is known.
        var keys = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (LeafConfigFieldDef f in fields)
        {
            if (f.DependsOn is not null && !keys.Contains(f.DependsOn))
            {
                error = $"field '{f.Key}': dependsOn '{f.DependsOn}', which is not a field here";
                return null;
            }
        }

        // The optionals are named, so inserting another one cannot silently shift the ones after it.
        return new LeafConfigDescriptor(
            raw.SchemaVersion, raw.Id!, raw.DisplayName!, raw.Unit!, raw.Role!,
            raw.OnDemand, applyMode, floors, groups, fields,
            ReadOnly: raw.ReadOnly, ReadOnlyReason: raw.ReadOnlyReason);
    }

    private static bool Required(string? value, string what, ref string? error)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return true;
        error = $"missing {what}";
        return false;
    }

    // The on-disk shape, all-nullable so a missing field is a validation error with a name rather than a
    // deserialization exception. Unknown JSON properties are ignored by design — the format is additive.
    private sealed record RawDescriptor(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        string? Id,
        string? DisplayName,
        string? Unit,
        string? Role,
        bool OnDemand,
        string? ApplyMode,
        IReadOnlyList<RawFloorSource>? FloorSources,
        IReadOnlyList<RawGroup>? Groups,
        IReadOnlyList<RawField>? Fields,
        bool ReadOnly = false,
        string? ReadOnlyReason = null);

    private sealed record RawFloorSource(string? Kind, string? Path);

    private sealed record RawGroup(string? Id, string? Label, int Order);

    private sealed record RawField(
        string? Key,
        string? Env,
        string? Label,
        string? Description,
        string? Group,
        string? Type,
        string? Default,
        IReadOnlyList<string>? Values,
        double? Min,
        double? Max,
        string? Unit,
        string? Risk,
        string? PairedApiKey,
        string? DependsOn);
}
