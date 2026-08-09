using System.Text.Json;
using System.Text.RegularExpressions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;

using TheKrystalShip.KGSM.Auth;

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
    /// <summary>
    /// Keys the framework resolves for us. They are settable and described, but they are not
    /// <see cref="ApiSettings"/> properties, so the property-level checks skip them. Named by prefix
    /// rather than allowed by a pattern over everything, so the exception cannot quietly widen.
    /// </summary>
    private static bool IsFrameworkKey(string key) =>
        key.StartsWith("Logging__", StringComparison.Ordinal)
        || key.StartsWith("Kestrel__", StringComparison.Ordinal)
        || key.StartsWith("MetricsThresholds__", StringComparison.Ordinal)
        || key == "AllowedHosts";

    /// <summary>
    /// Settable keys the Control Panel deliberately does not describe. The threshold rules are an
    /// array of objects, which an override file cannot express one key at a time, and the framework's
    /// own plumbing is not operator configuration in the sense this panel means.
    /// </summary>
    private static bool IsNotPanelConfiguration(string key) =>
        key.StartsWith("Kestrel__", StringComparison.Ordinal)
        || key.StartsWith("MetricsThresholds__", StringComparison.Ordinal)
        || key == "AllowedHosts"
        || key.StartsWith("Logging__LogLevel__Microsoft", StringComparison.Ordinal);

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

    private static string SettingsPath() => Path.Combine(RepoRoot(), "src", "Api", "kgsm-api.settings.json");

    private static JsonDocument SettingsDoc() =>
        JsonDocument.Parse(File.ReadAllText(SettingsPath()),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    /// <summary>
    /// Every environment variable that can set something, derived from the settings file itself by
    /// walking it to its leaves and joining each path with <c>__</c> — exactly the spelling
    /// configuration binds. A key absent here binds to nothing, whatever names it.
    /// </summary>
    private static HashSet<string> SettableEnvKeys()
    {
        Assert.True(File.Exists(SettingsPath()), $"the settings file is missing: {SettingsPath()}");
        using JsonDocument doc = SettingsDoc();

        var found = new HashSet<string>(StringComparer.Ordinal);
        static void Walk(JsonElement node, string prefix, HashSet<string> into)
        {
            foreach (JsonProperty prop in node.EnumerateObject())
            {
                string key = prefix.Length == 0 ? prop.Name : $"{prefix}__{prop.Name}";
                if (prop.Value.ValueKind == JsonValueKind.Object) Walk(prop.Value, key, into);
                else into.Add(key);
            }
        }
        Walk(doc.RootElement, string.Empty, found);

        Assert.NotEmpty(found);   // a scan that finds nothing would pass every check below vacuously
        return found;
    }

    /// <summary>The env-var spelling of every property the API binds.</summary>
    /// <summary>
    /// The keys of the shared <c>KgsmAuth</c> block that <em>this</em> surface's descriptor declares.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived from the type, because the section binds a <em>map</em> of
    /// applications keyed by provider name — there is no property per provider to reflect over, which
    /// is exactly what lets a host be wired to another one with no rebuild here. What belongs in this
    /// list is what an operator can set from this leaf's configuration page.
    /// </remarks>
    private static readonly string[] SharedAuthKeysThisApiReads =
        ["Providers__discord__ClientId", "Providers__discord__ClientSecret"];

    /// <summary>
    /// Every key the settings file can bind, across BOTH bound types. The <c>Api</c> section is this
    /// API's own; <c>KgsmAuth</c> is the ecosystem's shared authorization block, bound from the same
    /// file to a type in <c>TheKrystalShip.KGSM.Auth</c> so every surface on the host reads the same
    /// keys. A section declared in the file but not bound anywhere would silently drop.
    /// </summary>
    private static HashSet<string> SettingsPropertyKeys() =>
    [
        .. typeof(ApiSettings).GetProperties().Select(p => $"{ApiSettings.Section}__{p.Name}"),
        .. SharedAuthKeysThisApiReads.Select(k => $"{KgsmAuthOptions.Section}__{k}"),
    ];

    /// <summary>The declared value of one <c>Api</c> key, rendered the way a descriptor default is
    /// (always a string), or null when it is blank — which the descriptor spells as no default.</summary>
    private static string? DeclaredDefault(string property)
    {
        using JsonDocument doc = SettingsDoc();
        JsonElement v = doc.RootElement.GetProperty(ApiSettings.Section).GetProperty(property);
        string? text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => v.GetRawText(),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ── Coverage: the descriptor and the code agree, both ways ───────────────

    [Fact]
    public void Every_configurable_key_is_described()
    {
        var described = Fields().Select(f => Str(f, "env")).ToHashSet(StringComparer.Ordinal);
        var missing = SettableEnvKeys()
            .Where(k => !described.Contains(k) && !IsNotPanelConfiguration(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "these keys are settable but not described in deploy/kgsm-api.leaf.json, so the Control Panel " +
            "cannot show them:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_described_key_is_really_settable()
    {
        var settable = SettableEnvKeys();
        var fabricated = Fields()
            .Select(f => Str(f, "env"))
            .Where(e => !settable.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        Assert.True(fabricated.Count == 0,
            "these descriptor fields name keys the settings file does not declare, so they bind to nothing — an " +
            "override written for one would be reported as applied while changing nothing:\n  " +
            string.Join("\n  ", fabricated));
    }

    [Fact]
    public void Every_settings_key_binds_to_a_property()
    {
        var properties = SettingsPropertyKeys();
        var unbound = SettableEnvKeys()
            .Where(k => !IsFrameworkKey(k) && !properties.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(unbound.Count == 0,
            "these keys are declared in kgsm-api.settings.json but have no matching property on ApiSettings, " +
            "so binding silently drops them:\n  " + string.Join("\n  ", unbound));
    }

    [Fact]
    public void Every_settings_property_is_declared_in_the_file()
    {
        var settable = SettableEnvKeys();
        var undeclared = SettingsPropertyKeys().Where(k => !settable.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.True(undeclared.Count == 0,
            "these ApiSettings properties are missing from kgsm-api.settings.json, which is supposed to declare " +
            "the whole configurable surface with its defaults:\n  " + string.Join("\n  ", undeclared));
    }

    /// <summary>
    /// The env file is where a host actually sets things, so a key in it that the settings file does
    /// not declare is a knob an operator believes they set and that binds to nothing — the exact
    /// silent failure this whole arrangement exists to make impossible. Checked against the shipped
    /// example, which is the file <c>setup.sh</c> seeds a fresh host from.
    /// </summary>
    [Fact]
    public void The_env_example_sets_no_key_the_settings_file_does_not_declare()
    {
        string path = Path.Combine(RepoRoot(), "deploy", "kgsm-api.env.example");
        Assert.True(File.Exists(path), $"the env example is missing: {path}");

        var settable = SettableEnvKeys();
        var unknown = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            string t = line.Trim();
            // A commented line is documentation, not configuration — but the ones written as
            // "#Key=value" are meant to be uncommented, so they are checked too.
            if (t.Length == 0) continue;
            if (t.StartsWith('#')) t = t[1..].TrimStart();

            int eq = t.IndexOf('=');
            if (eq <= 0) continue;

            // An EnvironmentFile assignment has no space around the '=', so a commented sentence that
            // happens to contain one ("# Cert = Let's Encrypt via certbot") is prose, not a setting.
            string key = t[..eq];
            if (key.Length == 0 || char.IsWhiteSpace(key[^1]) || key.Contains(' ')) continue;

            // Host/runtime variables, read by the .NET host itself before any of our configuration
            // exists. They are real settings, just not ours to declare.
            if (key is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT") continue;
            if (key.StartsWith("DOTNET_", StringComparison.Ordinal)) continue;

            if (!settable.Contains(key)) unknown.Add(key);
        }

        Assert.True(unknown.Count == 0,
            "these keys are set in deploy/kgsm-api.env.example but declared nowhere in " +
            "kgsm-api.settings.json, so they bind to nothing:\n  " + string.Join("\n  ", unknown.Distinct()));
    }

    /// <summary>
    /// A descriptor default is what the Control Panel shows an operator as "what this is if you set
    /// nothing", so it has to be the value the settings file actually declares — not a second, separately
    /// maintained copy of it. Two of these had drifted before the check existed: the bind address and the
    /// Steam CDN base both named values the API has not used in some time.
    /// </summary>
    [Fact]
    public void Every_described_default_is_the_declared_one()
    {
        var wrong = new List<string>();
        foreach (JsonElement f in Fields())
        {
            string env = Str(f, "env");
            if (!env.StartsWith($"{ApiSettings.Section}__", StringComparison.Ordinal)) continue;

            string? declared = DeclaredDefault(env[(ApiSettings.Section.Length + 2)..]);
            string? described = OptionalStr(f, "default");
            if (declared != described)
                wrong.Add($"{Str(f, "key")}: descriptor says {described ?? "<none>"}, settings file declares {declared ?? "<blank>"}");
        }

        Assert.True(wrong.Count == 0,
            "these descriptor defaults disagree with kgsm-api.settings.json, so the Control Panel would show an " +
            "operator a default this API does not use:\n  " + string.Join("\n  ", wrong));
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

    /// <summary>Every secret this API holds — the signing key, the Discord client secret, the relay
    /// and cluster secrets — must be typed as one, or a read would echo it back to the panel.</summary>
    [Fact]
    public void Credentials_are_typed_as_secrets()
    {
        string[] mustBeSecret =
        [
            "Api__SigningKey",
            "KgsmAuth__Providers__discord__ClientSecret",
            "Api__AssistantRelaySecret",
            "Api__ClusterSecret",
            "Api__ClusterSecretPrevious",
            "Api__RawgApiKey",
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
