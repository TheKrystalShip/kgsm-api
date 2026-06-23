using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Builds this host's installable-game catalog (<c>GET /library</c>) for the M8·a read surface — a scrape of
/// the kgsm engine's blueprints via kgsm-lib <see cref="IBlueprintService"/>, joined with this host's cached
/// RAWG.io cover/metadata (<see cref="RawgStore"/>), mapped to the honest <see cref="LibraryEntry"/> shape.
/// No leaf join, no mutation, no fabricated field. Cover/hero/description/genres/tags come purely from the
/// cached RAWG row (hydrated by <see cref="RawgHydrationWorker"/>); with no cache they degrade to null/[].
/// </summary>
/// <remarks>
/// <para>
/// The blueprint read (<see cref="IBlueprintService.ListDetailed"/>) is a synchronous process spawn (it shells
/// <c>kgsm.sh</c>), so it runs on the thread pool and never blocks the request thread — the same posture as
/// <c>ServerAggregator</c>. <see cref="IBlueprintService"/> is a <em>transient</em> kgsm-lib service, resolved
/// per-call from the provider and only registered when the engine is provisioned; an unconfigured engine
/// degrades to an empty catalog with a one-time log (the engine is base, not a §4·b leaf).
/// </para>
/// <para>
/// The RAWG-row read degrades <strong>independently</strong> of the blueprint read: a DB failure (or the
/// table simply being empty) leaves cover/hero null while the catalog still renders. The absolute cover/hero
/// URLs are built from a request-derived (or <c>KGSM_API_PUBLIC_BASE_URL</c>) base URL passed in by the
/// controller — never reaching for <c>HttpContext</c> across the thread-pool boundary.
/// </para>
/// </remarks>
public sealed class LibraryAggregator
{
    private readonly IServiceProvider _services;
    private readonly RawgStore _rawg;
    private readonly ILogger<LibraryAggregator> _logger;

    // Latch so a persistent engine misconfiguration is logged once, not on every request.
    private int _engineUnavailableLogged;

    public LibraryAggregator(IServiceProvider services, RawgStore rawg, ILogger<LibraryAggregator> logger)
    {
        _services = services;
        _rawg = rawg;
        _logger = logger;
    }

    /// <summary>
    /// Build the catalog (the <c>GET /library</c> body). <paramref name="query"/> is an optional
    /// case-insensitive substring filter over the entry id and display name; <c>null</c>/blank returns the
    /// whole catalog. <paramref name="baseUrl"/> is the absolute origin (<c>{scheme}://{host}</c> or the
    /// configured public base) the cover/hero serving URLs are built from.
    /// </summary>
    public async Task<IReadOnlyList<LibraryEntry>> GetLibraryAsync(string? query, string baseUrl, CancellationToken ct)
    {
        // The RAWG cache read degrades independently of the blueprint read (a DB failure must not blank the
        // catalog) — fetch it first, honest-empty on failure.
        IReadOnlyDictionary<string, RawgEntry> rawgRows = await ReadRawgRowsAsync(ct).ConfigureAwait(false);

        IReadOnlyList<LibraryEntry> entries =
            await Task.Run(() => ReadCatalog(rawgRows, baseUrl), ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim();
            entries = [.. entries.Where(e =>
                e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))];
        }

        return entries;
    }

    private async Task<IReadOnlyDictionary<string, RawgEntry>> ReadRawgRowsAsync(CancellationToken ct)
    {
        try
        {
            return await _rawg.GetAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The cache being unreadable must degrade cover/hero to null, never blank the whole catalog.
            _logger.LogWarning(ex, "RAWG cache read failed; serving the catalog without cover/metadata.");
            return new Dictionary<string, RawgEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>The kgsm-lib blueprint read → the mapped, deterministically-ordered catalog.</summary>
    private IReadOnlyList<LibraryEntry> ReadCatalog(IReadOnlyDictionary<string, RawgEntry> rawgRows, string baseUrl)
    {
        // IBlueprintService is transient and only registered when the engine is provisioned; resolve
        // optionally so an unconfigured engine degrades to an empty catalog rather than throwing.
        var blueprints = _services.GetService(typeof(IBlueprintService)) as IBlueprintService;
        if (blueprints is null)
        {
            if (Interlocked.Exchange(ref _engineUnavailableLogged, 1) == 0)
                _logger.LogWarning(
                    "kgsm engine is not configured (KGSM_API_KGSM_PATH is empty) — /library will be empty.");
            return [];
        }

        try
        {
            Dictionary<string, Blueprint> catalog = blueprints.ListDetailed();
            var entries = new List<LibraryEntry>(catalog.Count);
            foreach ((string id, Blueprint bp) in catalog)
            {
                string entryId = string.IsNullOrWhiteSpace(id) ? bp.Name : id;
                rawgRows.TryGetValue(entryId, out RawgEntry? row);
                entries.Add(MapEntry(id, bp, row, baseUrl));
            }

            // Deterministic order so polling/diffing and the SPA list are stable.
            entries.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
            return entries;
        }
        catch (Exception ex)
        {
            // A list endpoint failing closed to empty is honest (it reports nothing, not a fabricated
            // value); surface it so an operator can see the blueprint read broke.
            _logger.LogWarning(ex, "kgsm blueprint read failed; serving an empty catalog.");
            return [];
        }
    }

    /// <summary>
    /// Map one kgsm-lib <see cref="Blueprint"/> (+ its optional cached RAWG row) to the honest
    /// <see cref="LibraryEntry"/>. Pure: the row IS the existence record (the worker is the sole writer and
    /// only sets <c>CoverFile</c>/<c>HeroFile</c> after the bytes land), so a non-empty file name → build the
    /// absolute serving URL; no <c>File.Exists</c> here (the serving endpoint does the real existence check).
    /// </summary>
    internal static LibraryEntry MapEntry(string id, Blueprint bp, RawgEntry? row, string baseUrl)
    {
        // id: prefer the dictionary key (the install key); fall back to the blueprint's own Name.
        string entryId = string.IsNullOrWhiteSpace(id) ? bp.Name : id;

        // name: the curated display name when present, else the id — never a guessed label.
        string name = string.IsNullOrWhiteSpace(bp.Metadata?.DisplayName)
            ? entryId
            : bp.Metadata!.DisplayName!;

        // The blueprint's declared default ports — already the canonical structured form (kgsm emits
        // [{start,end,protocol}] on `blueprints --json`, kgsm-lib types it List<PortMapping>), so the
        // API just projects to the DTO; it never parses a port string. Empty when none declared.
        IReadOnlyList<LibraryPort> ports =
        [
            .. bp.Ports.Select(static m => new LibraryPort(m.Start, m.End, m.Protocol)),
        ];

        var specs = new LibrarySpecs(
            MaxPlayers: bp.Metadata?.MaxPlayers,
            MinRamMb: bp.Metadata?.MinRamMb,
            RecommendedRamMb: bp.Metadata?.RecommendedRamMb,
            BaseDiskMb: bp.Metadata?.BaseDiskMb);

        // cover/hero: an absolute serving URL only when the cache row recorded a landed image file; else null
        // (no source / unresolved) — never a steam-cdn or media.rawg.io hotlink. The cover came from Steam
        // (capsule) or RAWG (fallback); either way the byte serving is the same self-hosted endpoint.
        string? cover = string.IsNullOrWhiteSpace(row?.CoverFile)
            ? null
            : ImageUrl(baseUrl, entryId, RawgCache.CoverSlot);
        string? hero = string.IsNullOrWhiteSpace(row?.HeroFile)
            ? null
            : ImageUrl(baseUrl, entryId, RawgCache.HeroSlot);

        // description precedence: curated blueprint description → cleaned RAWG description (stored cleaned by
        // the worker) → null. Never fabricated.
        string? description =
            !string.IsNullOrWhiteSpace(bp.Metadata?.Description) ? bp.Metadata!.Description!.Trim()
            : !string.IsNullOrWhiteSpace(row?.Description) ? row!.Description
            : null;

        IReadOnlyList<string> genres = RawgStore.DeserializeList(row?.Genres);
        IReadOnlyList<string> tags = RawgStore.DeserializeList(row?.Tags);

        return new LibraryEntry(
            Id: entryId,
            Name: name,
            Type: bp.BlueprintType == BlueprintType.Container ? "container" : "native",
            // Honest null for a non-Steam blueprint (never a fabricated "0" sentinel).
            SteamAppId: string.IsNullOrWhiteSpace(bp.SteamAppId) ? null : bp.SteamAppId,
            ClientSteamAppId: string.IsNullOrWhiteSpace(bp.ClientSteamAppId) ? null : bp.ClientSteamAppId,
            IsSteamAccountRequired: bp.IsSteamAccountRequired,
            Ports: ports,
            Specs: specs,
            Cover: cover,
            Hero: hero,
            Description: description,
            Genres: genres,
            Tags: tags,
            // The blueprint's curated RAWG slug, or null when it declares none (never name-guessed).
            RawgSlug: string.IsNullOrWhiteSpace(bp.Metadata?.RawgSlug) ? null : bp.Metadata!.RawgSlug);
    }

    // Build the absolute serving URL for an image slot: {base}/api/v1/library/{id}/{slot}. The base is already
    // trailing-slash-trimmed; the id is path-segment-escaped so a stray char can't break the URL.
    private static string ImageUrl(string baseUrl, string id, string slot) =>
        $"{baseUrl}/api/v1/library/{Uri.EscapeDataString(id)}/{slot}";
}
