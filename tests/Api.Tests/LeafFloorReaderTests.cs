using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The floor reader flattens a leaf's own settings file into the env-name → value map the panel resolves
/// provenance against. What it produces has to be the value the <em>leaf</em> reads, spelled the way every
/// other tier spells it — the panel compares tiers as strings, so a spelling difference is reported to an
/// operator as a difference in configuration.
/// </summary>
public sealed class LeafFloorReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("leaffloor").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The renderer the reader asks for its own override path, so that layer can be excluded from the
    // floor. Built the way the app builds it — nothing here is faked but the directory.
    private LeafFloorReader NewReader()
    {
        ApiOptions options = ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:LeafOverridesDir"] = Path.Combine(_dir, "overrides"),
                ["Api:LeafDescriptorDir"] = Path.Combine(_dir, "no-descriptors"),
            })
            .Build());
        var catalog = new LeafConfigCatalog(
            new LeafDescriptorStore(options, NullLogger<LeafDescriptorStore>.Instance), options);
        return new LeafFloorReader(
            options,
            new LeafOverrideRenderer(options, catalog, NullLogger<LeafOverrideRenderer>.Instance),
            NullLogger<LeafFloorReader>.Instance);
    }

    private static LeafConfigDescriptor Descriptor(string settingsPath) => new(
        SchemaVersion: 1, Id: "probe", DisplayName: "Probe", Unit: "kgsm-probe.service",
        Role: "a leaf", OnDemand: false, ApplyMode: "restart",
        FloorSources: [new LeafFloorSource("appsettings", settingsPath)],
        Groups: [], Fields: []);

    private LeafFloor ReadSettings(string json)
    {
        string settings = Path.Combine(_dir, "kgsm-probe.settings.json");
        File.WriteAllText(settings, json);
        return NewReader().Read(Descriptor(settings));
    }

    /// <summary>
    /// A JSON boolean flattens to <c>true</c>/<c>false</c> — the spelling the descriptor's default uses,
    /// the leaf's own parser writes, and the API writes when it renders an override. Left to
    /// <c>JsonElement.ToString()</c> it would arrive as <c>"True"</c>, which no other tier ever produces:
    /// the panel would compare a floor of "True" against a default of "true", find them different, and
    /// render a switch the leaf has ON as off — the panel misreporting what is running.
    /// </summary>
    [Fact]
    public void JsonBooleans_FlattenToTheCanonicalLowercaseSpelling()
    {
        LeafFloor floor = ReadSettings("""
            { "Discord": { "Announce": { "Started": true, "Ready": false } } }
            """);

        Assert.True(floor.Complete);
        Assert.Equal("true", floor.Values["Discord__Announce__Started"]);
        Assert.Equal("false", floor.Values["Discord__Announce__Ready"]);
    }

    /// <summary>Nesting maps to the <c>__</c> separator IConfiguration uses, at any depth.</summary>
    [Fact]
    public void NestedObjects_FlattenWithTheEnvSeparator()
    {
        LeafFloor floor = ReadSettings("""
            { "KGSM": { "Path": "/usr/local/bin/kgsm" }, "Logging": { "LogLevel": { "Default": "Information" } } }
            """);

        Assert.Equal("/usr/local/bin/kgsm", floor.Values["KGSM__Path"]);
        Assert.Equal("Information", floor.Values["Logging__LogLevel__Default"]);
    }

    /// <summary>Numbers and strings keep their literal text — only booleans needed canonicalizing.</summary>
    [Fact]
    public void NumbersAndStrings_AreCarriedVerbatim()
    {
        LeafFloor floor = ReadSettings("""
            { "A": { "Count": 300, "Ratio": 0.3, "Name": "kgsm", "Blank": "" } }
            """);

        Assert.Equal("300", floor.Values["A__Count"]);
        Assert.Equal("0.3", floor.Values["A__Ratio"]);
        Assert.Equal("kgsm", floor.Values["A__Name"]);
        Assert.Equal("", floor.Values["A__Blank"]);
    }

    /// <summary>
    /// A settings file is allowed comments and trailing commas, because the configuration provider the leaf
    /// itself loads it with accepts them — every leaf in this ecosystem ships an annotated one.
    /// </summary>
    [Fact]
    public void AnnotatedSettingsFile_IsRead_NotReportedIncomplete()
    {
        LeafFloor floor = ReadSettings("""
            {
              // what the bot announces
              "Discord": { "Announce": { "Crashed": true, } },
            }
            """);

        Assert.True(floor.Complete);
        Assert.Equal("true", floor.Values["Discord__Announce__Crashed"]);
    }

    /// <summary>
    /// A file that is not there is a leaf that ships without one — the floor is still complete. Only a
    /// source that could not be READ makes what the leaf runs with genuinely unknown.
    /// </summary>
    [Fact]
    public void AMissingSettingsFile_LeavesTheFloorComplete()
    {
        LeafFloor floor = NewReader().Read(Descriptor(Path.Combine(_dir, "nope.json")));

        Assert.True(floor.Complete);
        Assert.Empty(floor.Values);
    }
}
