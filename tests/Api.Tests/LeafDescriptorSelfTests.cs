using System.Text.Json;
using System.Text.RegularExpressions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// This API ships a leaf config descriptor of its own (<c>deploy/kgsm-api.leaf.json</c>), so the Control
/// Panel can show what the API itself is configured with. These are the same anti-drift guards every other
/// leaf carries, pointed at this repo: a setting added without a descriptor entry fails here, and a
/// descriptor entry naming a setting nothing reads fails here too.
///
/// The descriptor declares itself <strong>read-only</strong>. Applying a change here means restarting this
/// service, which would kill the request asking for it — so the surface is published for reading and the
/// values are edited in the host's env file. That is a property of what this leaf is, not of how the host
/// was provisioned, which is why it is declared rather than inferred from a missing drop-in.
/// </summary>
public class LeafDescriptorSelfTests
{
    private const string EnvPrefix = "KGSM_API_";

    /// <summary>
    /// Real settings this API reads that are not <c>KGSM_API_*</c> literals in its source: the ecosystem
    /// logging level, resolved through Microsoft.Extensions.Logging. Named explicitly so the exception
    /// cannot quietly widen.
    /// </summary>
    private static readonly HashSet<string> FrameworkKeys = new(StringComparer.Ordinal)
    {
        "Logging__LogLevel__Default",
    };

    /// <summary>
    /// <c>KGSM_API_*</c> names that appear in the source but are not operator configuration, each for a
    /// stated reason. Describing one would put a control on the panel that configures nothing.
    /// </summary>
    private static readonly HashSet<string> NotConfiguration = new(StringComparer.Ordinal)
    {
        // Prefixes, not settings: one enumerates the KGSM_API_* space, the other names a family of keys
        // in a log line ("KGSM_API_AUTH_DISCORD_* are set"). Neither is a variable anything reads.
        "KGSM_API_",
        "KGSM_API_AUTH_DISCORD_",
    };

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "kgsm-api.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repo root (no kgsm-api.slnx above the test binary)");
        return dir!.FullName;
    }

    private static string DescriptorPath() => Path.Combine(RepoRoot(), "deploy", "kgsm-api.leaf.json");

    private static JsonElement Descriptor()
    {
        string path = DescriptorPath();
        Assert.True(File.Exists(path), $"the leaf descriptor is missing: {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static List<JsonElement> Fields() => [.. Descriptor().GetProperty("fields").EnumerateArray()];

    private static string Str(JsonElement field, string name) => field.GetProperty(name).GetString()!;

    private static string? OptionalStr(JsonElement field, string name) =>
        field.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;

    /// <summary>Every KGSM_API_* name that appears anywhere in this API's own source.</summary>
    private static HashSet<string> EnvKeysInSource()
    {
        string src = Path.Combine(RepoRoot(), "src", "Api");
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"KGSM_API_[A-Z0-9_]*"))
                found.Add(m.Value);
        }

        found.ExceptWith(NotConfiguration);
        Assert.NotEmpty(found);
        return found;
    }

    // ── Coverage: the descriptor and the code agree, both ways ───────────────

    [Fact]
    public void Every_setting_this_api_reads_is_described()
    {
        var described = Fields().Select(f => Str(f, "env")).ToHashSet(StringComparer.Ordinal);
        var missing = EnvKeysInSource().Where(k => !described.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "these settings are read by this API but not described in deploy/kgsm-api.leaf.json, so the " +
            "Control Panel cannot show them:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_described_setting_is_real()
    {
        var inSource = EnvKeysInSource();
        var fabricated = Fields()
            .Select(f => Str(f, "env"))
            .Where(e => !inSource.Contains(e) && !FrameworkKeys.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(fabricated.Count == 0,
            "these descriptor fields name settings this API does not read:\n  " + string.Join("\n  ", fabricated));
    }

    // ── Structure ────────────────────────────────────────────────────────────

    /// <summary>
    /// The descriptor has to survive this API's own parser — which is the thing that will read it in
    /// production. A rejected descriptor is skipped silently at runtime, so catching it here is the
    /// difference between a configuration page and an empty one.
    /// </summary>
    [Fact]
    public void Descriptor_parses_with_the_real_parser()
    {
        LeafConfigDescriptor? parsed = LeafConfigDescriptorParser.TryParse(
            File.ReadAllText(DescriptorPath()), out string? error);

        Assert.True(parsed is not null, $"this API's own descriptor does not parse: {error}");
        Assert.Equal("api", parsed!.Id);
        Assert.Equal("kgsm-api.service", parsed.Unit);
        Assert.False(parsed.OnDemand);
        Assert.True(parsed.ReadOnly, "this API cannot restart itself to apply a change, so it must declare itself read-only");
        Assert.False(string.IsNullOrWhiteSpace(parsed.ReadOnlyReason), "a read-only leaf owes the reason");
    }

    [Fact]
    public void Field_keys_are_unique()
    {
        var keys = Fields().Select(f => Str(f, "key")).ToList();
        var dupes = keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(dupes.Count == 0, "duplicate field keys: " + string.Join(", ", dupes));
        Assert.Equal(keys.Count, Fields().Select(f => Str(f, "env")).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_field_is_completely_described()
    {
        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");

            Assert.False(string.IsNullOrWhiteSpace(OptionalStr(f, "label")), $"{key}: no label");
            Assert.False(string.IsNullOrWhiteSpace(OptionalStr(f, "description")), $"{key}: no description");
            Assert.Contains(Str(f, "type"), LeafConfigFieldType.All);
            Assert.Contains(OptionalStr(f, "risk") ?? LeafConfigRisk.Safe,
                new[] { LeafConfigRisk.Safe, LeafConfigRisk.Wiring, LeafConfigRisk.Destructive });
        }
    }

    /// <summary>Every secret this API holds — the signing key, the Discord credentials, the relay and
    /// cluster secrets — must be typed as one, or a read would echo it back to the panel.</summary>
    [Fact]
    public void Credentials_are_typed_as_secrets()
    {
        string[] mustBeSecret =
        [
            "KGSM_API_AUTH_SIGNING_KEY",
            "KGSM_API_AUTH_DISCORD_CLIENT_SECRET",
            "KGSM_API_AUTH_DISCORD_BOT_TOKEN",
            "KGSM_API_ASSISTANT_RELAY_SECRET",
            "KGSM_API_CLUSTER_SECRET",
            "KGSM_API_CLUSTER_SECRET_PREVIOUS",
            "KGSM_API_RAWG_API_KEY",
        ];

        foreach (string env in mustBeSecret)
        {
            JsonElement f = Fields().Single(x => Str(x, "env") == env);
            Assert.Equal(LeafConfigFieldType.Secret, Str(f, "type"));
            Assert.False(f.TryGetProperty("default", out _), $"{env}: a secret must not carry a default");
        }
    }

    [Fact]
    public void Numeric_defaults_satisfy_their_own_bounds()
    {
        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");
            bool numeric = Str(f, "type") is LeafConfigFieldType.Int or LeafConfigFieldType.Duration
                or LeafConfigFieldType.Float;

            if (!numeric)
            {
                Assert.False(f.TryGetProperty("min", out _), $"{key}: min on a non-numeric field");
                Assert.False(f.TryGetProperty("max", out _), $"{key}: max on a non-numeric field");
                continue;
            }

            if (OptionalStr(f, "default") is not { } def)
                continue;

            Assert.True(double.TryParse(def, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value),
                $"{key}: default '{def}' is not a number");
            if (f.TryGetProperty("min", out JsonElement min))
                Assert.True(value >= min.GetDouble(), $"{key}: default {value} is below its own min");
            if (f.TryGetProperty("max", out JsonElement max))
                Assert.True(value <= max.GetDouble(), $"{key}: default {value} is above its own max");
        }
    }

    [Fact]
    public void Group_and_dependency_references_resolve()
    {
        JsonElement d = Descriptor();
        var groups = d.GetProperty("groups").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var keys = Fields().Select(f => Str(f, "key")).ToHashSet(StringComparer.Ordinal);

        foreach (JsonElement f in Fields())
        {
            string key = Str(f, "key");
            if (OptionalStr(f, "group") is { } group)
                Assert.True(groups.Contains(group), $"{key}: references group '{group}', which is not defined");
            if (OptionalStr(f, "dependsOn") is { } dep)
            {
                Assert.True(keys.Contains(dep), $"{key}: dependsOn '{dep}', which is not a field here");
                Assert.NotEqual(key, dep);
            }
        }
    }
}
