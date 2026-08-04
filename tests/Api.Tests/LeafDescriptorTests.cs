using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The descriptor half of the leaf config surface: parsing and validating a leaf's shipped
/// <c>&lt;leaf&gt;.json</c>, scanning the discovery directory, and reading a leaf's floor so each field can
/// report where its live value actually comes from. Pure — no factory, no DI, temp dirs only.
/// </summary>
public sealed class LeafDescriptorParseTests
{
    private const string Valid = """
    {
      "schemaVersion": 1,
      "id": "monitor",
      "displayName": "Monitor",
      "unit": "kgsm-monitor.service",
      "role": "Metrics",
      "onDemand": false,
      "applyMode": "restart",
      "floorSources": [{ "kind": "env-file", "path": "/etc/kgsm-monitor/kgsm-monitor.env" }],
      "groups": [{ "id": "sampling", "label": "Sampling", "order": 1 }],
      "fields": [
        { "key": "intervalMs", "env": "KGSM_MONITOR_INTERVAL_MS", "label": "Sample interval",
          "description": "How often.", "group": "sampling", "type": "int", "default": "1000",
          "min": 100, "unit": "ms", "risk": "safe" },
        { "key": "socketPath", "env": "KGSM_MONITOR_SOCKET", "label": "Socket", "description": "Where.",
          "type": "path", "risk": "wiring", "pairedApiKey": "Api:MonitorSocketPath",
          "dependsOn": "intervalMs" }
      ]
    }
    """;

    [Fact]
    public void Parses_a_valid_descriptor_with_all_field_metadata()
    {
        LeafConfigDescriptor? d = LeafConfigDescriptorParser.TryParse(Valid, out string? error);

        Assert.Null(error);
        Assert.NotNull(d);
        Assert.Equal("monitor", d.Id);
        Assert.Equal("kgsm-monitor.service", d.Unit);
        Assert.Equal("restart", d.ApplyMode);
        Assert.Equal(2, d.Fields.Count);
        Assert.Single(d.Groups);
        Assert.Equal("env-file", d.FloorSources[0].Kind);

        LeafConfigFieldDef interval = d.Field("intervalMs")!;
        Assert.Equal("KGSM_MONITOR_INTERVAL_MS", interval.EnvName);
        Assert.Equal("1000", interval.Default);
        Assert.Equal(100, interval.Min);
        Assert.Equal("ms", interval.Unit);
        Assert.Equal(LeafConfigRisk.Safe, interval.Risk);

        LeafConfigFieldDef socket = d.Field("socketPath")!;
        Assert.Equal(LeafConfigRisk.Wiring, socket.Risk);
        Assert.Equal("Api:MonitorSocketPath", socket.PairedApiKey);
        Assert.Equal("intervalMs", socket.DependsOn);
        Assert.Null(socket.Default);            // declares none → never fabricated
    }

    [Fact]
    public void Risk_defaults_to_safe_when_unstated()
    {
        LeafConfigDescriptor? d = LeafConfigDescriptorParser.TryParse(
            Valid.Replace("\"risk\": \"safe\"", "\"unknownFutureKey\": true"), out _);

        // An unknown key is ignored (the format is additive), and the missing risk defaults.
        Assert.Equal(LeafConfigRisk.Safe, d!.Field("intervalMs")!.Risk);
    }

    [Theory]
    // A version this API does not know may mean something else entirely — refuse rather than guess.
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2", "schemaVersion 2")]
    [InlineData("\"id\": \"monitor\"", "\"id\": \"\"", "missing id")]
    [InlineData("\"type\": \"int\"", "\"type\": \"quantum\"", "unknown type")]
    [InlineData("\"risk\": \"wiring\"", "\"risk\": \"scary\"", "unknown risk")]
    [InlineData("\"group\": \"sampling\"", "\"group\": \"nope\"", "undefined group")]
    [InlineData("\"dependsOn\": \"intervalMs\"", "\"dependsOn\": \"ghost\"", "dependsOn")]
    [InlineData("\"kind\": \"env-file\"", "\"kind\": \"telepathy\"", "unknown kind")]
    [InlineData("\"applyMode\": \"restart\"", "\"applyMode\": \"pray\"", "unknown applyMode")]
    [InlineData("\"key\": \"socketPath\"", "\"key\": \"intervalMs\"", "duplicate field key")]
    public void Rejects_with_a_reason(string find, string replace, string expected)
    {
        LeafConfigDescriptor? d = LeafConfigDescriptorParser.TryParse(Valid.Replace(find, replace), out string? error);

        Assert.Null(d);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_malformed_json_without_throwing()
    {
        Assert.Null(LeafConfigDescriptorParser.TryParse("{ not json", out string? error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void Rejects_an_enum_with_no_values()
    {
        Assert.Null(LeafConfigDescriptorParser.TryParse(
            Valid.Replace("\"type\": \"int\"", "\"type\": \"enum\""), out string? error));
        Assert.Contains("enum needs values", error);
    }

    // ── The directory scan ────────────────────────────────────────────────────

    [Fact]
    public void Store_scans_the_directory_and_isolates_a_bad_descriptor()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "monitor.json"), Valid);
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ nope");
        // Right shape, wrong filename: reachable as 'imposter' but describing itself as 'monitor'.
        File.WriteAllText(Path.Combine(dir, "imposter.json"), Valid);

        LeafDescriptorStore store = StoreFor(dir);

        // One leaf shipping a bad file must not cost every other leaf its config surface.
        Assert.Single(store.All);
        Assert.NotNull(store.For("monitor"));
        Assert.Null(store.For("broken"));
        Assert.Null(store.For("imposter"));
    }

    [Fact]
    public void Store_is_empty_and_quiet_when_no_leaf_has_shipped_one()
    {
        LeafDescriptorStore store = StoreFor(Path.Combine(TempDir(), "does-not-exist"));

        Assert.Empty(store.All);
        Assert.Null(store.For("monitor"));
    }

    // ── The floor ─────────────────────────────────────────────────────────────

    [Fact]
    public void Floor_reads_a_unit_its_environment_files_and_its_drop_ins()
    {
        string dir = TempDir();
        string unit = Path.Combine(dir, "kgsm-monitor.service");
        string envFile = Path.Combine(dir, "monitor.env");
        string dropInDir = Path.Combine(dir, "kgsm-monitor.service.d");
        Directory.CreateDirectory(dropInDir);

        File.WriteAllText(unit, $"""
            [Service]
            Environment=KGSM_MONITOR_SOCKET=/run/kgsm-monitor/metrics.sock
            Environment="KGSM_MONITOR_IFACE_DENY=veth docker" KGSM_MONITOR_HOST_ID=hotrod
            # a comment
            EnvironmentFile=-{envFile}
            """);
        File.WriteAllText(envFile, """
            # host overrides
            KGSM_MONITOR_INTERVAL_MS=2000
            export KGSM_MONITOR_DB_PATH="/var/lib/kgsm-monitor/metrics.db"
            """);
        File.WriteAllText(Path.Combine(dropInDir, "90-local.conf"),
            "[Service]\nEnvironment=KGSM_MONITOR_MAINT_MS=30000\n");

        LeafFloor floor = ReaderFor(dir).Read(Descriptor(dir, [("systemd-unit", unit)]));

        Assert.True(floor.Complete);
        Assert.Equal("/run/kgsm-monitor/metrics.sock", floor.Values["KGSM_MONITOR_SOCKET"]);
        Assert.Equal("veth docker", floor.Values["KGSM_MONITOR_IFACE_DENY"]);   // quoted, space preserved
        Assert.Equal("hotrod", floor.Values["KGSM_MONITOR_HOST_ID"]);           // second assignment on one line
        Assert.Equal("2000", floor.Values["KGSM_MONITOR_INTERVAL_MS"]);         // via EnvironmentFile=
        Assert.Equal("/var/lib/kgsm-monitor/metrics.db", floor.Values["KGSM_MONITOR_DB_PATH"]);
        Assert.Equal("30000", floor.Values["KGSM_MONITOR_MAINT_MS"]);           // via a drop-in
    }

    [Fact]
    public void Floor_excludes_this_apis_own_override_layer()
    {
        string dir = TempDir();
        string overridesDir = Path.Combine(dir, "overrides");
        Directory.CreateDirectory(overridesDir);
        // Exactly what the renderer writes, referenced exactly as the API's drop-in references it.
        File.WriteAllText(Path.Combine(overridesDir, "monitor.env"), "KGSM_MONITOR_INTERVAL_MS=5000\n");

        string unit = Path.Combine(dir, "kgsm-monitor.service");
        File.WriteAllText(unit, $"""
            [Service]
            Environment=KGSM_MONITOR_INTERVAL_MS=1500
            EnvironmentFile=-{Path.Combine(overridesDir, "monitor.env")}
            """);

        LeafFloor floor = ReaderFor(dir, overridesDir).Read(Descriptor(dir, [("systemd-unit", unit)]));

        // The override layer is the override provenance tier. Folding it in would make every overridden key
        // look like the leaf had been configured that way by hand.
        Assert.Equal("1500", floor.Values["KGSM_MONITOR_INTERVAL_MS"]);
    }

    [Fact]
    public void Floor_flattens_appsettings_the_way_IConfiguration_maps_env_vars()
    {
        string dir = TempDir();
        string settings = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(settings, """
            { "Rag": { "Enabled": true }, "Logging": { "LogLevel": { "Default": "Warning" } } }
            """);

        LeafFloor floor = ReaderFor(dir).Read(Descriptor(dir, [("appsettings", settings)]));

        Assert.True(floor.Complete);
        Assert.Equal("True", floor.Values["Rag__Enabled"]);
        Assert.Equal("Warning", floor.Values["Logging__LogLevel__Default"]);
    }

    [Fact]
    public void Floor_is_incomplete_when_a_declared_source_cannot_be_read()
    {
        string dir = TempDir();

        LeafFloor floor = ReaderFor(dir).Read(
            Descriptor(dir, [("systemd-unit", Path.Combine(dir, "absent.service"))]));

        // "I could not read it" is not "it sets nothing" — the difference is what licenses falling through
        // to the descriptor default, so it has to survive to the caller.
        Assert.False(floor.Complete);
        Assert.Empty(floor.Values);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"kgsm-api-desc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ApiOptions Options(string descriptorDir, string dropInDir, string overridesDir) =>
        ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:LeafDescriptorDir"] = descriptorDir,
                ["Api:LeafDropInDir"] = dropInDir,
                ["Api:LeafOverridesDir"] = overridesDir,
            })
            .Build());

    private static LeafDescriptorStore StoreFor(string dir) =>
        new(Options(dir, dir, dir), NullLogger<LeafDescriptorStore>.Instance);

    private static LeafFloorReader ReaderFor(string dir, string? overridesDir = null)
    {
        ApiOptions opts = Options(dir, dir, overridesDir ?? Path.Combine(dir, "overrides"));
        var catalog = new LeafConfigCatalog(new LeafDescriptorStore(opts, NullLogger<LeafDescriptorStore>.Instance), opts);
        var renderer = new LeafOverrideRenderer(opts, catalog, NullLogger<LeafOverrideRenderer>.Instance);
        return new LeafFloorReader(opts, renderer, NullLogger<LeafFloorReader>.Instance);
    }

    private static LeafConfigDescriptor Descriptor(string dir, (string Kind, string Path)[] sources) =>
        new(1, "monitor", "Monitor", "kgsm-monitor.service", "Metrics", false, "restart",
            [.. sources.Select(s => new LeafFloorSource(s.Kind, s.Path))],
            [],
            [new LeafConfigFieldDef("intervalMs", "KGSM_MONITOR_INTERVAL_MS", "I", "d", LeafConfigFieldType.Int)]);
}
