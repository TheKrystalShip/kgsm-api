using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M8·a coverage for the installable-game catalog (<c>GET /library</c>). The mapping/filter/degrade
/// logic is proven at the aggregator level with a fake <see cref="IBlueprintService"/> wrapped in a
/// <see cref="BlueprintCache"/> (the populated path a box with no curated metadata can't show live),
/// mirroring <c>NetworkAggregatorTests</c>. The load-bearing honesty invariants: a non-Steam blueprint
/// maps to <c>null</c> (never a fabricated <c>"0"</c>); an uncurated metadata field is <c>null</c>
/// (never a fabricated <c>0</c>); the display name falls back to the id (never guessed);
/// <c>cover</c>/<c>rawgSlug</c> are reserved-null; ports are structured at the chokepoint. The live
/// wire shape (real ~29 blueprints, camelCase JSON) is smoke's job.
/// </summary>
public sealed class LibraryAggregatorTests
{
    [Fact]
    public async Task Maps_a_blueprint_to_the_honest_shape_with_structured_ports()
    {
        var bp = new Blueprint
        {
            Name = "7dtd",
            // Already structured (kgsm emits [{start,end,protocol}] on `blueprints --json`).
            Ports =
            [
                new PortMapping { Start = 26900, End = 26903, Protocol = "tcp" },
                new PortMapping { Start = 26900, End = 26903, Protocol = "udp" },
            ],
            BlueprintType = BlueprintType.Native,
            SteamAppId = "294420",
            ClientSteamAppId = "251570",
            IsSteamAccountRequired = false,
            Metadata = null, // uncurated — the live reality across every blueprint today
        };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["7dtd"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));

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
        // No cached RAWG row → cover/hero null, genres/tags [] (honest empty, never null), no slug curated.
        Assert.Null(e.Cover);
        Assert.Null(e.Hero);
        Assert.Null(e.Description);
        Assert.Empty(e.Genres);
        Assert.Empty(e.Tags);
        Assert.Null(e.RawgSlug);
    }

    [Fact]
    public async Task A_container_with_blank_steam_fields_maps_to_null_not_zero()
    {
        var bp = new Blueprint
        {
            Name = "abioticfactor",
            Ports = [new PortMapping { Start = 7777, End = 7777, Protocol = "udp" }],
            BlueprintType = BlueprintType.Container,
            SteamAppId = "",
            ClientSteamAppId = "",
        };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["abioticfactor"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));

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
            Ports = [new PortMapping { Start = 2456, End = 2457, Protocol = "udp" }],
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

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));

        Assert.Equal("Valheim", e.Name);              // curated display name wins over the id
        Assert.Equal(10, e.Specs.MaxPlayers);
        Assert.Equal(2048, e.Specs.MinRamMb);
        Assert.Equal(4096, e.Specs.RecommendedRamMb);
        Assert.Equal(1024, e.Specs.BaseDiskMb);
    }

    [Fact]
    public async Task Structured_blueprint_ports_project_one_to_one_to_LibraryPort()
    {
        // kgsm + kgsm-lib own port parsing; the catalog just projects the structured PortMapping
        // list to the DTO. A range stays a range (not expanded per-port) on the library surface.
        var bp = new Blueprint
        {
            Name = "g",
            Ports = [new PortMapping { Start = 34197, End = 34197, Protocol = "tcp" }],
            BlueprintType = BlueprintType.Native,
        };
        LibraryAggregator agg = Aggregator(new FakeBlueprints(new() { ["g"] = bp }));

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));

        Assert.Equal(new[] { new LibraryPort(34197, 34197, "tcp") }, e.Ports);
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

        Assert.Equal(new[] { "factorio" }, (await agg.GetLibraryAsync("FACT", BaseUrl, default)).Select(e => e.Id));
        // matches the display name even though the id is "minecraft"
        Assert.Equal(new[] { "minecraft" }, (await agg.GetLibraryAsync("java", BaseUrl, default)).Select(e => e.Id));
        Assert.Empty(await agg.GetLibraryAsync("zzz", BaseUrl, default));
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

        Assert.Equal(new[] { "ark", "minecraft", "valheim" }, (await agg.GetLibraryAsync(null, BaseUrl, default)).Select(e => e.Id));
    }

    [Fact]
    public async Task Engine_unconfigured_degrades_to_an_empty_catalog()
    {
        // Empty BlueprintCache — the engine-is-base degrade, not a 500.
        var cache = new BlueprintCache(new ServiceCollection().BuildServiceProvider(),
            TestOptions(), NullLogger<BlueprintCache>.Instance);
        // Don't call StartAsync — cache stays empty (engine never configured).
        var agg = new LibraryAggregator(cache, NewStore(), NullLogger<LibraryAggregator>.Instance);
        Assert.Empty(await agg.GetLibraryAsync(null, BaseUrl, default));
    }

    [Fact]
    public async Task A_failing_blueprint_read_degrades_to_an_empty_catalog()
    {
        LibraryAggregator agg = Aggregator(new FakeBlueprints(throwOnList: true));
        Assert.Empty(await agg.GetLibraryAsync(null, BaseUrl, default));
    }

    [Fact]
    public async Task A_seeded_RAWG_row_drives_cover_hero_description_genres_tags_as_absolute_urls()
    {
        var bp = new Blueprint
        {
            Name = "factorio",
            Ports = [new PortMapping { Start = 34197, End = 34197, Protocol = "udp" }],
            BlueprintType = BlueprintType.Native,
            Metadata = new BlueprintMetadata { RawgSlug = "factorio" }, // curated slug surfaces on the wire
        };
        RawgStore store = NewStore();
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "factorio",
            Slug = "factorio",
            Description = "Build and maintain factories.",
            Genres = RawgStore.SerializeList(["Strategy", "Simulation"]),
            Tags = RawgStore.SerializeList(["Automation", "Crafting"]),
            CoverFile = "factorio_cover.jpg",
            HeroFile = "factorio_hero.jpg",
            FetchedAt = DateTimeOffset.UtcNow,
            Status = "ok",
        });
        var agg = new LibraryAggregator(
            NewCache(new FakeBlueprints(new() { ["factorio"] = bp })),
            store, NullLogger<LibraryAggregator>.Instance);

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));

        Assert.Equal("factorio", e.RawgSlug);
        // Absolute, directly-renderable serving URLs (not a media.rawg.io hotlink).
        Assert.Equal($"{BaseUrl}/api/v1/library/factorio/cover", e.Cover);
        Assert.Equal($"{BaseUrl}/api/v1/library/factorio/hero", e.Hero);
        Assert.Equal("Build and maintain factories.", e.Description);
        Assert.Equal(new[] { "Strategy", "Simulation" }, e.Genres);
        Assert.Equal(new[] { "Automation", "Crafting" }, e.Tags);
    }

    [Fact]
    public async Task A_row_with_no_cover_file_keeps_cover_null_but_still_surfaces_metadata()
    {
        // RAWG had description/genres but no image (unreleased game) → metadata shows, cover/hero stay null.
        var bp = new Blueprint { Name = "romestead", Ports = [], BlueprintType = BlueprintType.Native,
            Metadata = new BlueprintMetadata { RawgSlug = "romestead" } };
        RawgStore store = NewStore();
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "romestead", Slug = "romestead",
            Description = "An upcoming settlement builder.",
            Genres = RawgStore.SerializeList(["Strategy"]), Tags = RawgStore.SerializeList([]),
            CoverFile = null, HeroFile = null, FetchedAt = DateTimeOffset.UtcNow, Status = "ok",
        });
        var agg = new LibraryAggregator(
            NewCache(new FakeBlueprints(new() { ["romestead"] = bp })),
            store, NullLogger<LibraryAggregator>.Instance);

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));
        Assert.Null(e.Cover);
        Assert.Null(e.Hero);
        Assert.Equal("An upcoming settlement builder.", e.Description);
        Assert.Equal(new[] { "Strategy" }, e.Genres);
        Assert.Empty(e.Tags);
    }

    [Fact]
    public async Task Curated_blueprint_description_wins_over_the_RAWG_row()
    {
        // Description precedence: curated blueprint metadata.description → RAWG → null.
        var bp = new Blueprint { Name = "valheim", Ports = [], BlueprintType = BlueprintType.Native,
            Metadata = new BlueprintMetadata { Description = "Curated blurb.", RawgSlug = "valheim" } };
        RawgStore store = NewStore();
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "valheim", Slug = "valheim", Description = "RAWG blurb.",
            Genres = RawgStore.SerializeList([]), Tags = RawgStore.SerializeList([]),
            FetchedAt = DateTimeOffset.UtcNow, Status = "ok",
        });
        var agg = new LibraryAggregator(
            NewCache(new FakeBlueprints(new() { ["valheim"] = bp })),
            store, NullLogger<LibraryAggregator>.Instance);

        LibraryEntry e = Assert.Single(await agg.GetLibraryAsync(null, BaseUrl, default));
        Assert.Equal("Curated blurb.", e.Description);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private const string BaseUrl = "http://test-host:8080";

    private static LibraryAggregator Aggregator(IBlueprintService blueprints) =>
        new(NewCache(blueprints), NewStore(), NullLogger<LibraryAggregator>.Instance);

    // Build a pre-populated BlueprintCache from a fake IBlueprintService. StartAsync kicks the
    // initial refresh so GetAll() returns the catalog before the test reads it.
    private static BlueprintCache NewCache(IBlueprintService? blueprints)
    {
        var sc = new ServiceCollection();
        if (blueprints is not null) sc.AddSingleton(blueprints);
        IServiceProvider sp = sc.BuildServiceProvider();
        var cache = new BlueprintCache(sp, TestOptions(), NullLogger<BlueprintCache>.Instance);
        cache.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return cache;
    }

    private static ApiOptions TestOptions() => new()
    {
        HostId = "test", HostLabel = "test",
        MonitorSocketPath = "", WatchdogSocketPath = "", AssistantBaseUrl = "", AssistantRelaySecret = "",
        FirewallSocketPath = "", KgsmPath = "", KgsmSocketPath = "",
        BlueprintCacheTtlSeconds = 60,
        LogSources = [], JournalctlPath = "journalctl", SystemctlPath = "systemctl", LogReadTimeoutMs = 5000,
        RawgApiKey = "", RawgCacheDir = Path.GetTempPath(), PublicBaseUrl = "",
        SteamCdnBaseUrl = "", SteamCoversDisabled = true,
        LibraryRefreshIntervalDays = 7, LibraryRefreshHour = 6,
        FilesMaxEntries = 200, FilesMaxEditBytes = 2 * 1024 * 1024,
        LeafOverridesDir = "/tmp/kgsm-api-test-overrides", LeafApplyCanaryMs = 15000,
        DomainPollMs = 5000, MetricsPollMs = 1000,
        MetricsHistoryEnabled = false, MetricsHistoryDb = "",
        MetricsPersistMs = 15000, MetricsRawRetentionHours = 24,
        MetricsRollupStepMin = 5, MetricsRollupRetentionDays = 30, MetricsMaintenanceMs = 60000,
        AuthDisabled = true, SigningKey = "", DiscordClientId = "", DiscordClientSecret = "",
        DiscordRedirectUri = "", DiscordBotToken = "", DiscordGuildId = "", AuthFrontendUrl = "",
        RoleAdminIds = [], RoleOperatorIds = [], RoleViewerIds = [],
    };

    // A RawgStore over a fresh per-call on-disk temp SQLite DB (the project's "tests use a fresh temp DB"
    // convention — parallel-safe, no shared state). EnsureCreated builds the rawg_entry table on first use;
    // an empty store is the no-cache case the bare-shape tests exercise. The file is left in the OS temp dir
    // (tiny; cleaned by the OS) — the same throwaway posture as smoke's SMOKE_DB.
    private static RawgStore NewStore()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"rawgtest-{Guid.NewGuid():N}.db");
        var sp = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        return new RawgStore(sp.GetRequiredService<IServiceScopeFactory>());
    }

    private static Blueprint Bp(string name, string? display = null) => new()
    {
        Name = name,
        Ports = [new PortMapping { Start = 1000, End = 1000, Protocol = "tcp" }],
        BlueprintType = BlueprintType.Native,
        Metadata = display is null ? null : new BlueprintMetadata { DisplayName = display },
    };

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

    [Fact]
    public async Task Cover_NoBearer_404_not_401()
    {
        // The image endpoints are [AllowAnonymous] (a CSS background:url / <img> never sends the bearer) —
        // so with NO token they 404 (no cached image) rather than 401. That 404-not-401 is the proof the
        // anonymous override beat the class [Authorize] + the global FallbackPolicy.
        HttpResponseMessage cover = await Client().GetAsync("/api/v1/library/nope/cover");
        Assert.Equal(HttpStatusCode.NotFound, cover.StatusCode);

        HttpResponseMessage hero = await Client().GetAsync("/api/v1/library/nope/hero");
        Assert.Equal(HttpStatusCode.NotFound, hero.StatusCode);
    }

    [Theory]
    // The {id} is untrusted on the anonymous route; a traversal must never escape the cache root. ASP.NET
    // routing blocks most of these, but the controller's path-containment guard is the belt-and-braces (it
    // returns NotFound, indistinguishable from a genuine miss — no info leak). All must be 404, never a file.
    [InlineData("/api/v1/library/..%2f..%2f..%2fetc%2fpasswd/cover")]
    [InlineData("/api/v1/library/%2e%2e%2f%2e%2e%2fappsettings/cover")]
    [InlineData("/api/v1/library/..%5c..%5cwindows/hero")]
    public async Task Image_path_traversal_attempts_404(string url)
    {
        HttpResponseMessage resp = await Client().GetAsync(url);
        // 404 (guarded / no such image) — crucially NOT 200 with file bytes.
        Assert.True(
            resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"expected 404/400 for {url}, got {(int)resp.StatusCode}");
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
    }
}
