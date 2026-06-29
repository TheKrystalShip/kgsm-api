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

    // --- the decoupled cover sources (Steam authority, RAWG fallback) ------------------------------

    [Fact]
    public async Task Steam_capsule_is_the_cover_when_the_blueprint_has_a_client_steam_appid()
    {
        var rawg = new FakeRawg
        {
            Games =
            {
                ["factorio"] = new RawgGame
                {
                    BackgroundImage = "https://media.rawg.io/factorio_cover.jpg",
                    BackgroundImageAdditional = "https://media.rawg.io/factorio_hero.jpg",
                    DescriptionRaw = "Build factories.",
                },
            },
        };
        var steam = new FakeSteam { Covers = { ["427520"] = SteamCoverBytes } };
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, steam, store, Bp("factorio", slug: "factorio", steamAppId: "427520"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("factorio");
        Assert.NotNull(row);
        Assert.Equal("ok", row!.Status);
        Assert.Equal("factorio_cover.jpg", row.CoverFile);   // same on-disk name regardless of source
        Assert.Equal("factorio_hero.jpg", row.HeroFile);     // hero still comes from RAWG
        Assert.Equal("Build factories.", row.Description);   // metadata still from RAWG
        // The cover bytes are STEAM's, not RAWG's — Steam is the cover authority.
        Assert.Equal(SteamCoverBytes, File.ReadAllBytes(RawgCache.FilePath(_cacheDir, "factorio", RawgCache.CoverSlot)));
        Assert.Equal(1, steam.Calls);
    }

    [Fact]
    public async Task Rawg_background_is_the_cover_fallback_when_the_game_is_not_on_steam()
    {
        var rawg = new FakeRawg
        {
            Games = { ["minecraft"] = new RawgGame { BackgroundImage = "https://media.rawg.io/mc_cover.jpg" } },
        };
        var steam = new FakeSteam(); // enabled, but the blueprint has no usable steam appid
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, steam, store, Bp("minecraft", slug: "minecraft", steamAppId: "0"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("minecraft");
        Assert.NotNull(row);
        Assert.Equal("minecraft_cover.jpg", row!.CoverFile); // file name is {id}_cover.jpg regardless of source
        Assert.Equal(RawgImageBytes, File.ReadAllBytes(RawgCache.FilePath(_cacheDir, "minecraft", RawgCache.CoverSlot)));
        Assert.Equal(0, steam.Calls); // appid "0" (not Steam) → Steam never asked
    }

    [Fact]
    public async Task Rawg_background_is_the_fallback_when_the_steam_capsule_404s()
    {
        var rawg = new FakeRawg
        {
            Games = { ["someserver"] = new RawgGame { BackgroundImage = "https://media.rawg.io/ss_cover.jpg" } },
        };
        var steam = new FakeSteam(); // enabled, Covers empty → a valid appid still 404s (returns null)
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, steam, store, Bp("someserver", slug: "someserver", steamAppId: "999999"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("someserver");
        Assert.NotNull(row);
        Assert.Equal("someserver_cover.jpg", row!.CoverFile);
        Assert.Equal(RawgImageBytes, File.ReadAllBytes(RawgCache.FilePath(_cacheDir, "someserver", RawgCache.CoverSlot)));
        Assert.Equal(1, steam.Calls); // Steam WAS tried (valid appid) and fell back on the 404
    }

    [Fact]
    public async Task Steam_cover_lands_with_no_rawg_key_and_records_cover_only()
    {
        var rawg = new FakeRawg { Enabled = false }; // no RAWG key — decoupled
        var steam = new FakeSteam { Covers = { ["427520"] = SteamCoverBytes } };
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, steam, store, Bp("factorio", slug: "factorio", steamAppId: "427520"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("factorio");
        Assert.NotNull(row);
        Assert.Equal("cover_only", row!.Status);             // RAWG was never consulted
        Assert.Equal("factorio_cover.jpg", row.CoverFile);   // but the Steam cover still landed
        Assert.Equal(SteamCoverBytes, File.ReadAllBytes(RawgCache.FilePath(_cacheDir, "factorio", RawgCache.CoverSlot)));
        Assert.Null(row.HeroFile);                           // no hero without RAWG
        Assert.Null(row.Description);                        // no metadata without RAWG
        Assert.Empty(RawgStore.DeserializeList(row.Genres)); // honest [] not null
        Assert.Equal(0, rawg.Calls);                         // truly decoupled — RAWG untouched
    }

    [Fact]
    public async Task No_row_is_written_when_both_sources_are_disabled()
    {
        var rawg = new FakeRawg { Enabled = false };
        var steam = new FakeSteam { Enabled = false };
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, steam, store, Bp("factorio", slug: "factorio", steamAppId: "427520"));

        await worker.SweepAsync(default);

        Assert.Null(await store.GetAsync("factorio")); // neither source on → not a hydration target
    }

    [Fact]
    public async Task A_cover_only_row_is_refetched_once_rawg_becomes_available()
    {
        RawgStore store = NewStore();
        // Seed a FRESH (<30d) Steam-only row — RAWG was off when it was first hydrated.
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "factorio", Slug = "factorio", Status = "cover_only", CoverFile = "factorio_cover.jpg",
            Genres = RawgStore.SerializeList([]), Tags = RawgStore.SerializeList([]),
            FetchedAt = DateTimeOffset.UtcNow,
        });
        var rawg = new FakeRawg { Games = { ["factorio"] = new RawgGame { DescriptionRaw = "Now with metadata." } } };
        var steam = new FakeSteam { Covers = { ["427520"] = SteamCoverBytes } };
        RawgHydrationWorker worker = Worker(rawg, steam, store, Bp("factorio", slug: "factorio", steamAppId: "427520"));

        await worker.SweepAsync(default);

        RawgEntry? row = await store.GetAsync("factorio");
        Assert.Equal("ok", row!.Status);                     // upgraded from cover_only
        Assert.Equal("Now with metadata.", row.Description); // RAWG metadata filled in (didn't wait 30d)
        Assert.Equal(1, rawg.Calls);
    }

    // --- the periodic-refresh mechanics (force + content-hash short-circuit) -----------------------

    [Fact]
    public async Task A_forced_sweep_refetches_even_a_fresh_row()
    {
        var rawg = new FakeRawg { Games = { ["valheim"] = new RawgGame { DescriptionRaw = "Refetched." } } };
        RawgStore store = NewStore();
        await store.UpsertAsync(new RawgEntry
        {
            BlueprintId = "valheim", Slug = "valheim", Description = "Old.",
            Genres = RawgStore.SerializeList([]), Tags = RawgStore.SerializeList([]),
            FetchedAt = DateTimeOffset.UtcNow, Status = "ok", // fresh (< interval)
        });
        RawgHydrationWorker worker = Worker(rawg, store, Bp("valheim", slug: "valheim"));

        await worker.SweepAsync(default, force: true); // the admin POST /library/refresh path

        Assert.Equal(1, rawg.Calls); // force bypassed the freshness gate (a non-forced sweep would skip it)
        Assert.Equal("Refetched.", (await store.GetAsync("valheim"))!.Description);
    }

    [Fact]
    public async Task An_unchanged_cover_is_not_rewritten_on_a_forced_refetch()
    {
        var rawg = new FakeRawg { Games = { ["factorio"] = new RawgGame { BackgroundImage = "https://media.rawg.io/c.jpg" } } };
        RawgStore store = NewStore();
        RawgHydrationWorker worker = Worker(rawg, store, Bp("factorio", slug: "factorio"));

        await worker.SweepAsync(default); // first hydration writes the cover
        string path = RawgCache.FilePath(_cacheDir, "factorio", RawgCache.CoverSlot);
        DateTime mtime1 = File.GetLastWriteTimeUtc(path);

        await Task.Delay(50); // a rewrite would advance the mtime past this delta
        await worker.SweepAsync(default, force: true); // forced re-fetch — identical bytes

        // Content-hash short-circuit: the file was NOT rewritten (no mtime churn → the serving ETag stays stable).
        Assert.Equal(mtime1, File.GetLastWriteTimeUtc(path));
    }

    // --- helpers -----------------------------------------------------------------------------------

    // Convenience: RAWG-only (Steam disabled) — the pre-decoupling default, so the original tests are unchanged.
    private RawgHydrationWorker Worker(IRawgClient rawg, RawgStore store, params (string Id, Blueprint Bp)[] blueprints) =>
        Worker(rawg, new FakeSteam { Enabled = false }, store, blueprints);

    private RawgHydrationWorker Worker(
        IRawgClient rawg, ISteamCoverClient steam, RawgStore store, params (string Id, Blueprint Bp)[] blueprints)
    {
        var options = TestOptions(_cacheDir);
        var catalog = blueprints.ToDictionary(b => b.Id, b => b.Bp);
        var sp = new ServiceCollection()
            .AddSingleton<IBlueprintService>(new FakeBlueprintsForWorker(catalog))
            .BuildServiceProvider();
        return new RawgHydrationWorker(options, rawg, steam, store, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RawgHydrationWorker>.Instance);
    }

    private static (string, Blueprint) Bp(string id, string? slug, string? steamAppId = null) =>
        (id, new Blueprint
        {
            Name = id,
            Ports = [],
            ClientSteamAppId = steamAppId ?? "",
            Metadata = slug is null ? new BlueprintMetadata() : new BlueprintMetadata { RawgSlug = slug },
        });

    private static ApiOptions TestOptions(string cacheDir) => new()
    {
        HostId = "test", HostLabel = "test",
        MonitorSocketPath = "", WatchdogSocketPath = "", AssistantBaseUrl = "", AssistantRelaySecret = "",
        FirewallSocketPath = "", KgsmPath = "/usr/bin/kgsm", KgsmSocketPath = "",
        LogSources = [], JournalctlPath = "journalctl", SystemctlPath = "systemctl", LogReadTimeoutMs = 5000,
        RawgApiKey = "test-key", RawgCacheDir = cacheDir, PublicBaseUrl = "",
        SteamCdnBaseUrl = "https://steamcdn.test/apps", // inert here: the worker uses the injected ISteamCoverClient fake
        LibraryRefreshIntervalDays = 7, LibraryRefreshHour = 6,
        FilesMaxEntries = 200, FilesMaxEditBytes = 2 * 1024 * 1024,
        DomainPollMs = 5000, MetricsPollMs = 1000,

        MetricsHistoryDb = "metrics.db", MetricsPersistMs = 15000, MetricsRawRetentionHours = 24,
        MetricsRollupStepMin = 5, MetricsRollupRetentionDays = 30, MetricsMaintenanceMs = 60000,

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

    // The distinctive byte buffers each source serves, so a test can read the on-disk cover and prove WHICH
    // source supplied it (Steam is the authority; RAWG is the fallback).
    private static readonly byte[] RawgImageBytes = [1, 2, 3, 4];
    private static readonly byte[] SteamCoverBytes = [9, 9, 9, 9];

    // Switch-on-input fake RAWG client (the project convention) — returns a canned game by slug (null = a 404),
    // and serves a non-empty byte buffer for any image URL. Enabled is settable so a no-key host can be modeled
    // (disabled ⇒ GetBySlugAsync returns null without a lookup, mirroring the real client).
    private sealed class FakeRawg : IRawgClient
    {
        public Dictionary<string, RawgGame> Games { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public bool Enabled { get; init; } = true;

        public Task<RawgGame?> GetBySlugAsync(string slug, CancellationToken ct)
        {
            if (!Enabled) return Task.FromResult<RawgGame?>(null);
            Calls++;
            return Task.FromResult(Games.TryGetValue(slug, out RawgGame? g) ? g : null);
        }

        public Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct) =>
            Task.FromResult<byte[]?>(RawgImageBytes); // non-empty → the worker writes the file
    }

    // Switch-on-input fake Steam cover client — Enabled is settable; returns the canned capsule bytes for an
    // app id present in Covers, else null (a 404 → the worker falls back to RAWG).
    private sealed class FakeSteam : ISteamCoverClient
    {
        public Dictionary<string, byte[]> Covers { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public bool Enabled { get; init; } = true;

        public Task<byte[]?> DownloadCoverAsync(string? clientSteamAppId, CancellationToken ct)
        {
            if (!Enabled || !SteamCoverClient.TryParseAppId(clientSteamAppId, out _)) return Task.FromResult<byte[]?>(null);
            Calls++;
            return Task.FromResult(clientSteamAppId is not null && Covers.TryGetValue(clientSteamAppId, out byte[]? b) ? b : null);
        }
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
