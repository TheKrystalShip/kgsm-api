using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Library;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The RAWG hydration worker (the M8·a library increment) — proven with a fake <see cref="IRawgClient"/> so
/// no live HTTP is touched (offline). The load-bearing honesty: a null slug → no row; a 404 → never wipe a
/// good row (record <c>not_found</c>); an ok fetch → a full row + the image bytes land on disk; opt-in (no
/// key → no-op).
/// </summary>
public sealed class RawgHydrationWorkerTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"rawgcache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task An_ok_fetch_writes_a_full_row_and_lands_the_image_bytes_on_disk()
    {
        var rawg = new FakeRawg
        {
            Games =
            {
                ["factorio"] = new RawgGame
                {
                    BackgroundImage = "https://media.rawg.io/factorio_cover.jpg",
                    BackgroundImageAdditional = "https://media.rawg.io/factorio_hero.jpg",
                    DescriptionRaw = "Build and maintain factories on an alien planet.",
                    Genres = [new RawgNamed { Name = "Strategy" }, new RawgNamed { Name = "Simulation" }],
                    Tags = [new RawgNamed { Name = "Automation" }, new RawgNamed { Name = "Crafting" }],
                },
            },
        };
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, store, Bp("factorio", slug: "factorio"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("factorio");
        Assert.NotNull(row);
        Assert.Equal("ok", row!.Status);
        Assert.Equal("factorio", row.Slug);
        Assert.Equal("Build and maintain factories on an alien planet.", row.Description);
        Assert.Equal(new[] { "Strategy", "Simulation" }, RawgStore.DeserializeList(row.Genres));
        Assert.Equal(new[] { "Automation", "Crafting" }, RawgStore.DeserializeList(row.Tags));
        Assert.Equal("factorio_cover.jpg", row.CoverFile);
        Assert.Equal("factorio_hero.jpg", row.HeroFile);
        // The bytes actually landed at the pinned cache path.
        Assert.True(File.Exists(RawgCache.FilePath(_cacheDir, "factorio", RawgCache.CoverSlot)));
        Assert.True(File.Exists(RawgCache.FilePath(_cacheDir, "factorio", RawgCache.HeroSlot)));
    }

    [Fact]
    public async Task A_blueprint_with_no_slug_produces_no_row()
    {
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(new FakeRawg(), store, Bp("noslug", slug: null));

        await worker.SweepAsync(default);

        Assert.Null(await store.GetAsync("noslug"));
    }

    [Fact]
    public async Task A_404_records_not_found_and_never_wipes_a_prior_good_row()
    {
        RawgStore store = NewStore();
        // Seed a prior good row (cover already cached).
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "gone", Slug = "gone", Description = "Previously good.",
            Genres = RawgStore.SerializeList(["Action"]), Tags = RawgStore.SerializeList([]),
            CoverFile = "gone_cover.jpg", FetchedAt = DateTimeOffset.UtcNow.AddDays(-40), Status = "ok",
        });
        // RAWG now 404s (FakeRawg has no entry for the slug → null).
        RawgHydrationWorker worker = Worker(new FakeRawg(), store, Bp("gone", slug: "gone"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("gone");
        Assert.NotNull(row);
        Assert.Equal("not_found", row!.Status);
        Assert.Equal("Previously good.", row.Description);   // never wiped
        Assert.Equal("gone_cover.jpg", row.CoverFile);       // the good cover survives
    }

    [Fact]
    public async Task A_fresh_row_is_not_refetched()
    {
        var rawg = new FakeRawg();
        rawg.Games["valheim"] = new RawgGame { DescriptionRaw = "Should not be fetched." };
        RawgStore store = NewStore();
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "valheim", Slug = "valheim", Description = "Fresh.",
            Genres = RawgStore.SerializeList([]), Tags = RawgStore.SerializeList([]),
            FetchedAt = DateTimeOffset.UtcNow, Status = "ok",   // fresh (< 30d)
        });
        RawgHydrationWorker worker = Worker(rawg, store, Bp("valheim", slug: "valheim"));

        await worker.SweepAsync(default);

        Assert.Equal(0, rawg.Calls);                            // the fresh row short-circuited the fetch
        Assert.Equal("Fresh.", (await store.GetAsync("valheim"))!.Description);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private RawgHydrationWorker Worker(IRawgClient rawg, RawgStore store, params (string Id, Blueprint Bp)[] blueprints)
    {
        var options = TestOptions(_cacheDir);
        var catalog = blueprints.ToDictionary(b => b.Id, b => b.Bp);
        var sp = new ServiceCollection()
            .AddSingleton<IBlueprintService>(new FakeBlueprintsForWorker(catalog))
            .BuildServiceProvider();
        return new RawgHydrationWorker(options, rawg, store, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RawgHydrationWorker>.Instance);
    }

    private static (string, Blueprint) Bp(string id, string? slug) =>
        (id, new Blueprint
        {
            Name = id,
            Ports = [],
            Metadata = slug is null ? new BlueprintMetadata() : new BlueprintMetadata { RawgSlug = slug },
        });

    private static ApiOptions TestOptions(string cacheDir) => new()
    {
        HostId = "test", HostLabel = "test",
        MonitorSocketPath = "", WatchdogSocketPath = "", AssistantBaseUrl = "", AssistantRelaySecret = "",
        FirewallSocketPath = "", KgsmPath = "/usr/bin/kgsm", KgsmSocketPath = "",
        RawgApiKey = "test-key", RawgCacheDir = cacheDir, PublicBaseUrl = "",
        AuthDisabled = true, SigningKey = "", DiscordClientId = "", DiscordClientSecret = "",
        DiscordRedirectUri = "", DiscordBotToken = "", DiscordGuildId = "", AuthFrontendUrl = "",
        RoleAdminIds = [], RoleOperatorIds = [], RoleViewerIds = [],
    };

    private static RawgStore NewStore()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"rawgwtest-{Guid.NewGuid():N}.db");
        var sp = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        return new RawgStore(sp.GetRequiredService<IServiceScopeFactory>());
    }

    // Switch-on-input fake RAWG client (the project convention) — Enabled, returns a canned game by slug
    // (null = a 404), and serves a tiny non-empty byte buffer for any image URL.
    private sealed class FakeRawg : IRawgClient
    {
        public Dictionary<string, RawgGame> Games { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public bool Enabled => true;

        public Task<RawgGame?> GetBySlugAsync(string slug, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Games.TryGetValue(slug, out RawgGame? g) ? g : null);
        }

        public Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct) =>
            Task.FromResult<byte[]?>([1, 2, 3, 4]); // non-empty → the worker writes the file
    }

    private sealed class FakeBlueprintsForWorker(Dictionary<string, Blueprint> detailed) : IBlueprintService
    {
        public Dictionary<string, Blueprint> ListDetailed() => detailed;
        public List<string> List() => throw new NotImplementedException();
        public List<string> ListDefault() => throw new NotImplementedException();
        public List<string> ListCustom() => throw new NotImplementedException();
        public Blueprint? GetInfo(string blueprintName) => throw new NotImplementedException();
        public string? FindPath(string blueprintName) => throw new NotImplementedException();
    }
}
