using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M8·a coverage for the installable-game catalog (<c>GET /library</c>). The mapping/filter/degrade
/// logic is proven at the aggregator level with a fake <see cref="IBlueprintService"/> (the populated
/// path a box with no curated metadata can't show live), mirroring <c>NetworkAggregatorTests</c>. The
/// load-bearing honesty invariants: a non-Steam blueprint maps to <c>null</c> (never a fabricated
/// <c>"0"</c>); an uncurated metadata field is <c>null</c> (never a fabricated <c>0</c>); the display
/// name falls back to the id (never guessed); <c>cover</c>/<c>rawgSlug</c> are reserved-null; ports are
/// structured at the chokepoint. The live wire shape (real ~29 blueprints, camelCase JSON) is smoke's job.
/// </summary>
public sealed class LibraryAggregatorTests
{
    [Fact]
    public async Task Maps_a_blueprint_to_the_honest_shape_with_structured_ports()
    {
        var bp = new Blueprint
        {
            Name = "7dtd",
            Ports = "26900:26903/tcp|26900:26903/udp",
            BlueprintType = BlueprintType.Native,
            SteamAppId = "294420",
            ClientSteamAppId = "251570",
            IsSteamAccountRequired = false,
            Metadata = null, // uncurated — the live reality across every blueprint today
        };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["7dtd"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, default));

        Assert.Equal("7dtd", e.Id);
        Assert.Equal("7dtd", e.Name);                 // no DisplayName → id fallback, never guessed
        Assert.Equal("native", e.Type);
        Assert.Equal("294420", e.SteamAppId);
        Assert.Equal("251570", e.ClientSteamAppId);
        Assert.False(e.IsSteamAccountRequired);
        Assert.Equal(
            new[] { new LibraryPort(26900, 26903, "tcp"), new LibraryPort(26900, 26903, "udp") },
            e.Ports);
        Assert.Null(e.Specs.MaxPlayers);              // uncurated metadata → null, never 0
        Assert.Null(e.Specs.MinRamMb);
        Assert.Null(e.Cover);                         // reserved (RAWG resolver is a later increment)
        Assert.Null(e.RawgSlug);                      // reserved
    }

    [Fact]
    public async Task A_container_with_blank_steam_fields_maps_to_null_not_zero()
    {
        var bp = new Blueprint
        {
            Name = "abioticfactor",
            Ports = "7777/udp",
            BlueprintType = BlueprintType.Container,
            SteamAppId = "",
            ClientSteamAppId = "",
        };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["abioticfactor"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, default));

        Assert.Equal("container", e.Type);
        Assert.Null(e.SteamAppId);                    // honest null, not the Server DTO's "0" sentinel
        Assert.Null(e.ClientSteamAppId);
        Assert.Equal(new[] { new LibraryPort(7777, 7777, "udp") }, e.Ports);
    }

    [Fact]
    public async Task Curated_metadata_drives_the_name_and_specs()
    {
        var bp = new Blueprint
        {
            Name = "valheim",
            Ports = "2456:2457/udp",
            BlueprintType = BlueprintType.Native,
            Metadata = new BlueprintMetadata
            {
                DisplayName = "Valheim",
                MaxPlayers = 10,
                MinRamMb = 2048,
                RecommendedRamMb = 4096,
                BaseDiskMb = 1024,
            },
        };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["valheim"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, default));

        Assert.Equal("Valheim", e.Name);              // curated display name wins over the id
        Assert.Equal(10, e.Specs.MaxPlayers);
        Assert.Equal(2048, e.Specs.MinRamMb);
        Assert.Equal(4096, e.Specs.RecommendedRamMb);
        Assert.Equal(1024, e.Specs.BaseDiskMb);
    }

    [Fact]
    public async Task A_protocol_less_blueprint_port_expands_to_tcp_and_udp()
    {
        var bp = new Blueprint { Name = "g", Ports = "34197", BlueprintType = BlueprintType.Native };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["g"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, default));

        Assert.Equal(
            new[] { new LibraryPort(34197, 34197, "tcp"), new LibraryPort(34197, 34197, "udp") },
            e.Ports);
    }

    [Fact]
    public async Task Query_filters_by_id_and_display_name_case_insensitively()
    {
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new()
        {
            ["valheim"] = Bp("valheim"),
            ["factorio"] = Bp("factorio"),
            ["minecraft"] = Bp("minecraft", display: "Minecraft Java"),
        }));

        Assert.Equal(new[] { "factorio" }, (await agg.GetLibraryAsync("FACT", default)).Select(e => e.Id));
        // matches the display name even though the id is "minecraft"
        Assert.Equal(new[] { "minecraft" }, (await agg.GetLibraryAsync("java", default)).Select(e => e.Id));
        Assert.Empty(await agg.GetLibraryAsync("zzz", default));
    }

    [Fact]
    public async Task Results_are_ordered_by_id()
    {
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new()
        {
            ["valheim"] = Bp("valheim"),
            ["ark"] = Bp("ark"),
            ["minecraft"] = Bp("minecraft"),
        }));

        Assert.Equal(new[] { "ark", "minecraft", "valheim" }, (await agg.GetLibraryAsync(null, default)).Select(e => e.Id));
    }

    [Fact]
    public async Task Engine_unconfigured_degrades_to_an_empty_catalog()
    {
        // No IBlueprintService resolvable — the engine-is-base degrade, not a 500.
        var agg = new LibraryAggregator(new StubProvider(null), NullLogger<LibraryAggregator>.Instance);
        Assert.Empty(await agg.GetLibraryAsync(null, default));
    }

    [Fact]
    public async Task A_failing_blueprint_read_degrades_to_an_empty_catalog()
    {
        LibraryAggregator agg = Aggregator(new FakeBlueprints(throwOnList: true));
        Assert.Empty(await agg.GetLibraryAsync(null, default));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static LibraryAggregator Aggregator(IBlueprintService blueprints) =>
        new(new StubProvider(blueprints), NullLogger<LibraryAggregator>.Instance);

    private static Blueprint Bp(string name, string? display = null) => new()
    {
        Name = name,
        Ports = "1000/tcp",
        BlueprintType = BlueprintType.Native,
        Metadata = display is null ? null : new BlueprintMetadata { DisplayName = display },
    };

    private sealed class StubProvider(IBlueprintService? blueprints) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IBlueprintService) ? blueprints : null;
    }

    // Switch-on-input fake (the project convention) — only ListDetailed is exercised by the aggregator.
    private sealed class FakeBlueprints : IBlueprintService
    {
        private readonly Dictionary<string, Blueprint> _detailed;
        private readonly bool _throw;

        public FakeBlueprints(Dictionary<string, Blueprint>? detailed = null, bool throwOnList = false)
        {
            _detailed = detailed ?? [];
            _throw = throwOnList;
        }

        public Dictionary<string, Blueprint> ListDetailed() =>
            _throw ? throw new InvalidOperationException("boom") : _detailed;

        public List<string> List() => throw new NotImplementedException();
        public List<string> ListDefault() => throw new NotImplementedException();
        public List<string> ListCustom() => throw new NotImplementedException();
        public Blueprint? GetInfo(string blueprintName) => throw new NotImplementedException();
        public string? FindPath(string blueprintName) => throw new NotImplementedException();
    }
}

/// <summary>
/// The <c>/library</c> auth gate + the engine-unprovisioned degrade-to-empty, proven through the real
/// pipeline (the <c>AuthTestFactory</c> leaves the engine unprovisioned). <c>401</c> (no bearer) vs
/// <c>403</c> (authenticated, tier 'none') is the load-bearing split, as elsewhere.
/// </summary>
public sealed class LibraryEndpointTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private HttpClient Client(string? token = null)
    {
        HttpClient c = factory.CreateClient();
        if (token is not null)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task NoToken_401()
    {
        HttpResponseMessage resp = await Client().GetAsync("/api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NoneTier_403()
    {
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.None)).GetAsync("/api/v1/library");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_EngineUnprovisioned_200_EmptyArray()
    {
        // Engine unprovisioned → an honest empty catalog, never a 500 (degrade-gracefully).
        HttpResponseMessage resp = await Client(factory.AccessToken(AuthTier.Viewer)).GetAsync("/api/v1/library");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("[]", (await resp.Content.ReadAsStringAsync()).Trim());
    }
}
