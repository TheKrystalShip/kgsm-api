using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Hydrates this host's library RAWG cache (the M8·a library increment) — <b>once on boot + every ~30 days</b>.
/// For each blueprint with a curated <c>metadata.rawg_slug</c> whose cache row is missing / stale (&gt;30d) /
/// pointing at a now-changed slug, it fetches the RAWG game, cleans the description, downloads the cover + hero
/// bytes to disk, and upserts the row. Registered hosted + singleton in <c>Startup.cs</c> like the other pumps;
/// it runs off the request path and <b>never blocks startup</b>. After hydration the runtime never touches RAWG.
/// </summary>
/// <remarks>
/// <para><b>Opt-in.</b> Blank <c>KGSM_API_RAWG_API_KEY</c> ⇒ the worker logs once and no-ops (cover/hero stay
/// null — the SPA's gradient fallback). This is the offline/test default.</para>
/// <para><b>Honest / never-fabricate.</b> A null slug → skipped (no row). A RAWG 404 / network failure → the
/// existing row is <b>never wiped</b>; we record <see cref="RawgEntry.Status"/> (<c>not_found</c>/<c>error</c>)
/// and keep whatever good bytes we already have. A game with no image leaves that slot's file null. Genres/tags
/// are stored as <c>"[]"</c> when none.</para>
/// <para><b>Bounded + gentle.</b> Blueprints are processed sequentially (≈29 games, one RAWG call each) with a
/// small inter-call delay so a cold hydration stays well under the free-tier budget; the loop is fully
/// cancellable on shutdown. The blueprint read degrades to "nothing to do" when the engine is unconfigured.</para>
/// </remarks>
public sealed class RawgHydrationWorker(
    ApiOptions options,
    IRawgClient rawg,
    RawgStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<RawgHydrationWorker> logger) : BackgroundService
{
    /// <summary>How old a cache row may be before a refresh re-fetches it (~monthly — decision 4).</summary>
    internal static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);

    /// <summary>The periodic refresh cadence (matches <see cref="RefreshAfter"/>).</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromDays(30);

    /// <summary>A small delay between per-game RAWG calls so a cold hydration is gentle on the free tier.</summary>
    private static readonly TimeSpan PerGameDelay = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!rawg.Enabled)
        {
            logger.LogInformation(
                "RAWG hydration is OFF (KGSM_API_RAWG_API_KEY is blank) — library cover/hero stay null (opt-in).");
            return;
        }

        try
        {
            // Sweep once on boot, then every ~30 days. Let the host finish coming up first.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(SweepInterval);
            do
            {
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "RAWG hydration sweep failed; will retry next cadence.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    /// <summary>Run one hydration sweep (boot/refresh). Internal so the test project can drive it
    /// deterministically (the AlertEngine-internal-Tick posture) without the boot delay / timer.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(options.RawgCacheDir);

        IReadOnlyList<(string Id, string Slug, string? Description)> targets = ReadTargets();
        if (targets.Count == 0) return;

        IReadOnlyDictionary<string, RawgEntry> existing = await store.GetAllAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int fetched = 0;
        foreach ((string id, string slug, string? _) in targets)
        {
            ct.ThrowIfCancellationRequested();

            existing.TryGetValue(id, out RawgEntry? row);
            if (!NeedsRefresh(row, slug, now)) continue;

            await HydrateOneAsync(id, slug, row, now, ct).ConfigureAwait(false);
            fetched++;
            await Task.Delay(PerGameDelay, ct).ConfigureAwait(false);
        }

        logger.LogInformation("RAWG hydration sweep done: {Fetched}/{Total} blueprint(s) (re)fetched.",
            fetched, targets.Count);
    }

    // A row needs a refresh if it's missing, the curated slug changed, or it's older than the refresh window.
    private static bool NeedsRefresh(RawgEntry? row, string slug, DateTimeOffset now)
    {
        if (row is null) return true;
        if (!string.Equals(row.Slug, slug, StringComparison.Ordinal)) return true;
        return now - row.FetchedAt >= RefreshAfter;
    }

    private async Task HydrateOneAsync(string id, string slug, RawgEntry? existing, DateTimeOffset now, CancellationToken ct)
    {
        RawgGame? game = await rawg.GetBySlugAsync(slug, ct).ConfigureAwait(false);
        if (game is null)
        {
            // 404 / network failure — NEVER wipe a previously-good row; just record the outcome + timestamp so
            // we don't hammer a missing slug every boot. A brand-new miss writes a sparse not_found row.
            RawgEntry miss = existing ?? new RawgEntry { BlueprintId = id };
            miss.Slug = slug;
            miss.FetchedAt = now;
            miss.Status = "not_found";
            miss.Genres ??= RawgStore.SerializeList([]);
            miss.Tags ??= RawgStore.SerializeList([]);
            await store.UpsertAsync(miss, ct).ConfigureAwait(false);
            return;
        }

        // Download the images (keep .jpg, never re-encode). A failed download leaves that slot null; we keep
        // a previously-good file name so a transient failure doesn't blank an existing cover.
        (string? coverFile, string? coverEtag) =
            await SaveImageAsync(id, RawgCache.CoverSlot, game.BackgroundImage, existing?.CoverFile, existing?.CoverEtag, ct)
                .ConfigureAwait(false);
        (string? heroFile, string? heroEtag) =
            await SaveImageAsync(id, RawgCache.HeroSlot, game.BackgroundImageAdditional, existing?.HeroFile, existing?.HeroEtag, ct)
                .ConfigureAwait(false);

        var row = new RawgEntry
        {
            BlueprintId = id,
            Slug = slug,
            Description = RawgDescription.Clean(game.DescriptionRaw),
            Genres = RawgStore.SerializeList(Names(game.Genres)),
            Tags = RawgStore.SerializeList(Names(game.Tags, take: 12)),
            CoverFile = coverFile,
            HeroFile = heroFile,
            CoverEtag = coverEtag,
            HeroEtag = heroEtag,
            Released = game.Released,
            Rating = game.Rating,
            Website = game.Website,
            FetchedAt = now,
            Status = "ok",
        };
        await store.UpsertAsync(row, ct).ConfigureAwait(false);
    }

    // Download + persist one image slot to {cacheDir}/{id}_{slot}.jpg, returning (fileName, etag) or (prior, prior)
    // when there's nothing new to fetch / the fetch failed (keep any existing good bytes).
    private async Task<(string? File, string? Etag)> SaveImageAsync(
        string id, string slot, string? url, string? priorFile, string? priorEtag, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return (priorFile, priorEtag); // RAWG had no image for this slot

        byte[]? bytes = await rawg.DownloadImageAsync(url, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return (priorFile, priorEtag); // transient failure — keep prior

        string path = RawgCache.FilePath(options.RawgCacheDir, id, slot);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

        // Content-hash ETag (the serving endpoint recomputes a size+mtime tag, but storing a content hash here
        // is the honest provenance record for a future "did the bytes change" check).
        string etag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16]}\"";
        return (RawgCache.FileName(id, slot), etag);
    }

    // The blueprints with a curated rawg_slug — the hydration targets. Degrades to empty when the engine is
    // unconfigured (resolve IBlueprintService optionally from a fresh scope; it's transient).
    private IReadOnlyList<(string Id, string Slug, string? Description)> ReadTargets()
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var blueprints = scope.ServiceProvider.GetService<IBlueprintService>();
        if (blueprints is null) return [];

        try
        {
            Dictionary<string, Blueprint> catalog = blueprints.ListDetailed();
            var targets = new List<(string, string, string?)>();
            foreach ((string id, Blueprint bp) in catalog)
            {
                string slug = bp.Metadata?.RawgSlug?.Trim() ?? "";
                if (slug.Length == 0) continue; // no slug → skip (honest, no row)
                string entryId = string.IsNullOrWhiteSpace(id) ? bp.Name : id;
                targets.Add((entryId, slug, bp.Metadata?.Description));
            }
            return targets;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RAWG hydration: blueprint read failed; nothing to hydrate this sweep.");
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
