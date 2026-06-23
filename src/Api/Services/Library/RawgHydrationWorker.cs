using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Hydrates this host's library cover/metadata cache (the M8·a library increment): a <b>boot sweep</b> plus a
/// <b>periodic refresh</b> that wakes at a configurable local hour (default 06:00) and re-fetches any row older
/// than a configurable interval (default 7 days = weekly). For each blueprint whose cache row is missing /
/// stale / pointing at a now-changed slug, it downloads the cover, the hero screenshot, and the
/// description/genres/tags to disk and upserts the row. Registered hosted + singleton in <c>Startup.cs</c>
/// (like the other pumps) so an admin <c>POST /library/refresh</c> can force an immediate full re-fetch. It
/// runs off the request path and <b>never blocks startup</b>.
/// </summary>
/// <remarks>
/// <para><b>Two decoupled sources.</b> The <b>cover</b> art has its own authority: <see cref="ISteamCoverClient"/>
/// fetches the Steam library capsule (the 2:3 portrait) keyed by the blueprint's <c>client_steam_app_id</c> —
/// <b>keyless</b>, so it works with no RAWG key. <see cref="IRawgClient"/> is the cover <b>fallback</b> (a game
/// not on Steam / no capsule) AND the sole authority for everything else (hero, description, genres, tags).
/// The two never gate each other.</para>
/// <para><b>Opt-in / off switch.</b> Steam covers are ON by default (keyless); RAWG is opt-in
/// (<c>KGSM_API_RAWG_API_KEY</c>). When <b>both</b> are off the worker logs once and no-ops. The periodic wake
/// is disabled by <c>KGSM_API_LIBRARY_REFRESH_INTERVAL_DAYS=0</c> (boot sweep + on-demand refresh only).</para>
/// <para><b>Honest / never-fabricate.</b> A Steam 404 → fall back to RAWG; a RAWG 404 / network failure → the
/// existing row is <b>never wiped</b>; we record <see cref="RawgEntry.Status"/> and keep whatever good bytes we
/// already have. A Steam-only hydration (no RAWG key) records <c>cover_only</c> so a later key fills the rest
/// without waiting out the refresh window. The cover source (steam/rawg) is <b>logged, never persisted</b>.</para>
/// <para><b>Cheap + safe to re-run.</b> A re-fetch only rewrites an image when the bytes actually changed
/// (content-hash short-circuit) so the periodic sweep doesn't churn mtimes / bust the serving ETag for static
/// art; writes are atomic (temp + rename) so a concurrent <c>GET …/cover</c> never sees a half-written JPEG.
/// One sweep runs at a time (boot, the timer, and a manual refresh share a gate).</para>
/// </remarks>
public sealed class RawgHydrationWorker(
    ApiOptions options,
    IRawgClient rawg,
    ISteamCoverClient steam,
    RawgStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<RawgHydrationWorker> logger) : BackgroundService
{
    /// <summary>A small delay between per-game fetches so a cold hydration is gentle on the RAWG free tier
    /// and the Steam CDN.</summary>
    private static readonly TimeSpan PerGameDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>One sweep at a time — the boot sweep, the daily timer, and a manual <c>POST /library/refresh</c>
    /// all funnel through this so two sweeps never write the cache concurrently.</summary>
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    /// <summary>The app-lifetime token (captured in <see cref="ExecuteAsync"/>) so a manual refresh kicked off
    /// the request thread is still cancelled on shutdown rather than leaking.</summary>
    private CancellationToken _appStopping = CancellationToken.None;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appStopping = stoppingToken;

        if (!rawg.Enabled && !steam.Enabled)
        {
            logger.LogInformation(
                "Library cover/metadata hydration is OFF (no RAWG key + Steam covers disabled) — cover/hero stay null.");
            return;
        }

        try
        {
            // Boot sweep — fill missing/stale rows shortly after startup (let the host finish coming up first).
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            await GuardedSweepAsync(force: false, stoppingToken).ConfigureAwait(false);

            if (options.LibraryRefreshIntervalDays <= 0)
            {
                logger.LogInformation(
                    "Library periodic refresh disabled (interval=0) — boot sweep + POST /library/refresh only.");
                return;
            }

            // Wake daily at the configured local hour; each wake re-fetches anything older than the interval.
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan wait = DelayUntilNextRun();
                logger.LogInformation("Next library refresh check at {Hour:00}:00 local (~{Hours:0.0}h).",
                    options.LibraryRefreshHour, wait.TotalHours);
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                await GuardedSweepAsync(force: false, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    /// <summary>
    /// Trigger an immediate, <b>forced</b> full re-fetch (the admin <c>POST /library/refresh</c>). Returns
    /// <c>false</c> if a sweep is already in flight (the caller returns <c>409</c>); otherwise it kicks the
    /// sweep onto a background task tied to the app lifetime and returns <c>true</c> (the caller returns
    /// <c>202</c> — the sweep runs off the request thread).
    /// </summary>
    public bool RequestRefresh()
    {
        if (!_sweepGate.Wait(0)) return false; // a sweep is already running
        _ = Task.Run(async () =>
        {
            try { await SweepAsync(_appStopping, force: true).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Manual library refresh failed.");
            }
            finally { _sweepGate.Release(); }
        });
        return true;
    }

    // Acquire the gate (non-blocking); skip if a sweep is already running, so the boot/timer sweeps never pile up.
    private async Task GuardedSweepAsync(bool force, CancellationToken ct)
    {
        if (!await _sweepGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            logger.LogInformation("Library refresh skipped — a sweep is already running.");
            return;
        }
        try { await SweepAsync(ct, force).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Library hydration sweep failed; will retry next cadence.");
        }
        finally { _sweepGate.Release(); }
    }

    /// <summary>Run one hydration sweep. <paramref name="force"/> re-fetches every target regardless of age
    /// (the manual refresh); otherwise only missing/slug-changed/cover_only/older-than-interval rows. Internal
    /// so the test project can drive it deterministically (the AlertEngine-internal-Tick posture) without the
    /// boot delay / timer.</summary>
    internal async Task SweepAsync(CancellationToken ct, bool force = false)
    {
        Directory.CreateDirectory(options.RawgCacheDir);

        IReadOnlyList<(string Id, string? Slug, string? SteamAppId)> targets = ReadTargets();
        if (targets.Count == 0) return;

        IReadOnlyDictionary<string, RawgEntry> existing = await store.GetAllAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int fetched = 0;
        foreach ((string id, string? slug, string? steamAppId) in targets)
        {
            ct.ThrowIfCancellationRequested();

            existing.TryGetValue(id, out RawgEntry? row);
            if (!NeedsRefresh(row, slug, now, force)) continue;

            await HydrateOneAsync(id, slug, steamAppId, row, now, ct).ConfigureAwait(false);
            fetched++;
            await Task.Delay(PerGameDelay, ct).ConfigureAwait(false);
        }

        logger.LogInformation("Library cover/metadata hydration sweep done: {Fetched}/{Total} blueprint(s) (re)fetched{Forced}.",
            fetched, targets.Count, force ? " (forced)" : "");
    }

    // A row needs a refresh if it's missing, the curated slug changed, it was a Steam-only ("cover_only")
    // hydration and RAWG is now configured (fill the rest without waiting), the caller forced it, or it's
    // older than the configured interval. interval <= 0 disables the pure-age refresh (missing/changed/forced
    // still apply — those are correctness, not staleness).
    private bool NeedsRefresh(RawgEntry? row, string? slug, DateTimeOffset now, bool force)
    {
        if (row is null) return true;
        if (!string.Equals(row.Slug, slug ?? "", StringComparison.Ordinal)) return true;
        if (row.Status == "cover_only" && rawg.Enabled && !string.IsNullOrWhiteSpace(slug)) return true;
        if (force) return true;
        if (options.LibraryRefreshIntervalDays <= 0) return false;
        return now - row.FetchedAt >= TimeSpan.FromDays(options.LibraryRefreshIntervalDays);
    }

    private async Task HydrateOneAsync(
        string id, string? slug, string? steamAppId, RawgEntry? existing, DateTimeOffset now, CancellationToken ct)
    {
        // RAWG is the authority for hero/description/genres/tags AND the cover fallback. A blank slug or a
        // disabled RAWG → a null game (the client returns null); we then rely on Steam for the cover and keep
        // any prior metadata. Independent of this, the cover prefers the Steam capsule below.
        RawgGame? game = await rawg.GetBySlugAsync(slug ?? "", ct).ConfigureAwait(false);
        bool rawgConsulted = rawg.Enabled && !string.IsNullOrWhiteSpace(slug);

        // COVER — Steam library capsule first (the authority), RAWG background_image as the fallback. A RAWG 404
        // still gets a Steam cover when the game is on Steam (the two are decoupled).
        (string? coverFile, string? coverEtag, string coverSource) =
            await AcquireCoverAsync(id, steamAppId, game?.BackgroundImage, existing, ct).ConfigureAwait(false);

        // HERO + the textual metadata stay RAWG-sourced.
        (string? heroFile, string? heroEtag) =
            await SaveImageAsync(id, RawgCache.HeroSlot, game?.BackgroundImageAdditional, existing?.HeroFile, existing?.HeroEtag, ct)
                .ConfigureAwait(false);

        RawgEntry row = existing ?? new RawgEntry { BlueprintId = id };
        row.BlueprintId = id;
        row.Slug = slug ?? "";
        row.CoverFile = coverFile;
        row.CoverEtag = coverEtag;
        row.HeroFile = heroFile;
        row.HeroEtag = heroEtag;
        row.FetchedAt = now;

        if (game is not null)
        {
            row.Description = RawgDescription.Clean(game.DescriptionRaw);
            row.Genres = RawgStore.SerializeList(Names(game.Genres));
            row.Tags = RawgStore.SerializeList(Names(game.Tags, take: 12));
            row.Released = game.Released;
            row.Rating = game.Rating;
            row.Website = game.Website;
            row.Status = "ok";
        }
        else
        {
            // RAWG 404/down OR not consulted (no slug / no key) — NEVER wipe previously-good metadata; just make
            // the JSON columns honest ([], never null). "not_found" only when we actually queried RAWG; else
            // "cover_only" (Steam-only — a later RAWG key fills the rest, see NeedsRefresh).
            row.Genres ??= RawgStore.SerializeList([]);
            row.Tags ??= RawgStore.SerializeList([]);
            row.Status = rawgConsulted ? "not_found" : "cover_only";
        }

        logger.LogDebug("Library hydrate {Id}: cover={CoverSource}, status={Status}.", id, coverSource, row.Status);
        await store.UpsertAsync(row, ct).ConfigureAwait(false);
    }

    // Acquire the cover bytes: Steam library capsule first (authority, keyed by the client appid), RAWG
    // background_image as the fallback, else keep any previously-good cover. Returns the on-disk file name +
    // etag + a source tag (logged only, never persisted). "kept" = nothing new landed this sweep.
    private async Task<(string? File, string? Etag, string Source)> AcquireCoverAsync(
        string id, string? steamAppId, string? rawgBackgroundUrl, RawgEntry? existing, CancellationToken ct)
    {
        if (steam.Enabled)
        {
            byte[]? steamBytes = await steam.DownloadCoverAsync(steamAppId, ct).ConfigureAwait(false);
            if (steamBytes is { Length: > 0 })
                return (RawgCache.FileName(id, RawgCache.CoverSlot),
                        await PersistImageAsync(id, RawgCache.CoverSlot, steamBytes, existing?.CoverEtag, ct).ConfigureAwait(false),
                        "steam");
        }

        if (rawg.Enabled && !string.IsNullOrWhiteSpace(rawgBackgroundUrl))
        {
            byte[]? rawgBytes = await rawg.DownloadImageAsync(rawgBackgroundUrl, ct).ConfigureAwait(false);
            if (rawgBytes is { Length: > 0 })
                return (RawgCache.FileName(id, RawgCache.CoverSlot),
                        await PersistImageAsync(id, RawgCache.CoverSlot, rawgBytes, existing?.CoverEtag, ct).ConfigureAwait(false),
                        "rawg");
        }

        return (existing?.CoverFile, existing?.CoverEtag, "kept");
    }

    // Download + persist a RAWG image slot to {cacheDir}/{id}_{slot}.jpg, returning (fileName, etag) or
    // (prior, prior) when there's nothing new to fetch / the fetch failed (keep any existing good bytes).
    private async Task<(string? File, string? Etag)> SaveImageAsync(
        string id, string slot, string? url, string? priorFile, string? priorEtag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return (priorFile, priorEtag); // RAWG had no image for this slot

        byte[]? bytes = await rawg.DownloadImageAsync(url, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return (priorFile, priorEtag); // transient failure — keep prior

        string etag = await PersistImageAsync(id, slot, bytes, priorEtag, ct).ConfigureAwait(false);
        return (RawgCache.FileName(id, slot), etag);
    }

    // Persist image bytes to {cacheDir}/{id}_{slot}.jpg (keep .jpg, never re-encode) and return a content-hash
    // ETag. Short-circuits when the bytes are unchanged (== priorEtag and the file still exists) so a periodic
    // re-fetch doesn't rewrite the file — which would bump its mtime and needlessly bust the serving endpoint's
    // size+mtime ETag for byte-identical art. The write is atomic (temp + rename) so a concurrent
    // GET /library/{id}/{slot} never observes a half-written JPEG.
    private async Task<string> PersistImageAsync(string id, string slot, byte[] bytes, string? priorEtag, CancellationToken ct)
    {
        string etag = ContentEtag(bytes);
        string path = RawgCache.FilePath(options.RawgCacheDir, id, slot);
        if (etag == priorEtag && File.Exists(path)) return etag; // unchanged — keep the existing file + mtime

        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true); // atomic rename on the same filesystem
        return etag;
    }

    private static string ContentEtag(byte[] bytes) =>
        $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}\"";

    // Delay from now until the next occurrence of the configured local hour (today if still ahead, else
    // tomorrow). Recomputed each loop iteration so it stays aligned across DST shifts.
    private TimeSpan DelayUntilNextRun()
    {
        DateTimeOffset now = DateTimeOffset.Now; // local
        var todayRun = new DateTimeOffset(now.Year, now.Month, now.Day, options.LibraryRefreshHour, 0, 0, now.Offset);
        DateTimeOffset next = now < todayRun ? todayRun : todayRun.AddDays(1);
        TimeSpan delay = next - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    // The hydration targets: each blueprint that has SOMETHING fetchable this sweep — a usable Steam appid
    // (cover, when Steam is on) and/or a curated rawg_slug (metadata + cover-fallback, when RAWG is on).
    // Degrades to empty when the engine is unconfigured (IBlueprintService is transient, resolved per-sweep).
    private IReadOnlyList<(string Id, string? Slug, string? SteamAppId)> ReadTargets()
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var blueprints = scope.ServiceProvider.GetService<IBlueprintService>();
        if (blueprints is null) return [];

        try
        {
            Dictionary<string, Blueprint> catalog = blueprints.ListDetailed();
            var targets = new List<(string, string?, string?)>();
            foreach ((string id, Blueprint bp) in catalog)
            {
                string slug = bp.Metadata?.RawgSlug?.Trim() ?? "";
                string appId = (bp.ClientSteamAppId ?? "").Trim();

                bool steamable = steam.Enabled && SteamCoverClient.TryParseAppId(appId, out _);
                bool rawgable = rawg.Enabled && slug.Length > 0;
                if (!steamable && !rawgable) continue; // nothing to fetch for this blueprint this sweep

                string entryId = string.IsNullOrWhiteSpace(id) ? bp.Name : id;
                targets.Add((entryId, slug.Length == 0 ? null : slug, appId.Length == 0 ? null : appId));
            }
            return targets;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Library hydration: blueprint read failed; nothing to hydrate this sweep.");
            return [];
        }
    }

    // RAWG named entities → distinct, non-blank names (top `take`, default all).
    private static IReadOnlyList<string> Names(List<RawgNamed>? items, int take = int.MaxValue)
    {
        if (items is null || items.Count == 0) return [];
        return [.. items
            .Select(n => n.Name?.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(take)];
    }
}
