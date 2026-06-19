using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Builds this host's installable-game catalog (<c>GET /library</c>) for the M8·a read surface — a
/// pure scrape of the kgsm engine's blueprints via kgsm-lib <see cref="IBlueprintService"/>, mapped
/// to the honest <see cref="LibraryEntry"/> shape. The catalog analog of the M1·a host scrape: no
/// leaf join, no mutation, no fabricated field. Cover-art resolution (<c>§3·i</c>) is deliberately a
/// later increment — every entry's <c>cover</c> is reserved-<c>null</c> here.
/// </summary>
/// <remarks>
/// <para>
/// The blueprint read (<see cref="IBlueprintService.ListDetailed"/>) is a synchronous process spawn
/// (it shells <c>kgsm.sh</c>), so it runs on the thread pool and never blocks the request thread —
/// the same posture as <c>ServerAggregator</c>. <see cref="IBlueprintService"/> is a
/// <em>transient</em> kgsm-lib service, resolved per-call from the provider and only registered when
/// the engine is provisioned; an unconfigured engine degrades to an empty catalog with a one-time
/// log (the engine is base, not a §4·b leaf — there is no "engine" capability to render absent).
/// </para>
/// </remarks>
public sealed class LibraryAggregator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LibraryAggregator> _logger;

    // Latch so a persistent engine misconfiguration is logged once, not on every request.
    private int _engineUnavailableLogged;

    public LibraryAggregator(IServiceProvider services, ILogger<LibraryAggregator> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Build the catalog (the <c>GET /library</c> body). <paramref name="query"/> is an optional
    /// case-insensitive substring filter over the entry id and display name; <c>null</c>/blank
    /// returns the whole catalog.
    /// </summary>
    public async Task<IReadOnlyList<LibraryEntry>> GetLibraryAsync(string? query, CancellationToken ct)
    {
        IReadOnlyList<LibraryEntry> entries = await Task.Run(ReadCatalog, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim();
            entries = [.. entries.Where(e =>
                e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))];
        }

        return entries;
    }

    /// <summary>The kgsm-lib blueprint read → the mapped, deterministically-ordered catalog.</summary>
    private IReadOnlyList<LibraryEntry> ReadCatalog()
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
                entries.Add(MapEntry(id, bp));

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

    /// <summary>Map one kgsm-lib <see cref="Blueprint"/> to the honest <see cref="LibraryEntry"/>.</summary>
    internal static LibraryEntry MapEntry(string id, Blueprint bp)
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
            // RESERVED at M8·a — RAWG cover resolution is its own later increment; never name-guessed.
            Cover: null,
            RawgSlug: null);
    }
}
