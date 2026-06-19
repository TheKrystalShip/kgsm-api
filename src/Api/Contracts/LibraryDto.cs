namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One installable game in the catalog — the <strong>honest realization</strong> of the
/// <c>GET /library</c> entry (<c>architecture.html §3·h/§3·i</c>), frozen at M8·a. Sourced purely
/// from the kgsm engine's blueprints (via kgsm-lib <c>IBlueprintService.ListDetailed</c>); a pure
/// read, the catalog analog of the M1·a host scrape (no mutation, no leaf join).
/// <para>
/// The aspirational surface asks for <c>cover</c>, <c>specs</c>, and <c>defaults</c>. We emit only
/// what the blueprint honestly backs today and reserve the rest — the never-fabricate invariant that
/// scrapped the old kgsm-api:
/// </para>
/// <list type="bullet">
///   <item><description><c>name</c> is the curated blueprint metadata display name when
///     present, else the blueprint <c>id</c> — the honest fallback, never a guessed display name
///     (blueprint metadata curation is deferred upstream, so every blueprint's metadata is null
///     today and <c>name == id</c>).</description></item>
///   <item><description><c>cover</c> is <strong>RESERVED — always null</strong> at M8·a. Cover-art
///     resolution (RAWG, server-side, key off-browser — <c>§3·i</c>) is its own later increment;
///     resolving only from an <em>exact</em> key (Steam App ID → Steam CDN) is honest, a fuzzy
///     name→RAWG match would mis-attribute the wrong game's art (fabrication-by-misattribution).</description></item>
///   <item><description><c>steamAppId</c>/<c>clientSteamAppId</c> are <c>null</c> for a non-Steam
///     blueprint (honest unknown over the <c>Server</c> DTO's <c>"0"</c> sentinel — a deliberate,
///     frozen choice for this new surface).</description></item>
///   <item><description><c>specs</c> keys are always present but every value is nullable and
///     <c>null</c> today (metadata uncurated upstream) — a <c>null</c> spec is "unknown", never a
///     fabricated 0.</description></item>
/// </list>
/// Keys are always present with explicit values (honest unknown over omission) so the SPA binds a
/// stable shape regardless of how sparse a given blueprint is.
/// </summary>
/// <param name="Id">The blueprint id — the catalog key AND the only field the installer needs
/// (<c>POST /servers { blueprint }</c>, M8·b).</param>
/// <param name="Name">Display name: the curated metadata display name, else <paramref name="Id"/>.</param>
/// <param name="Type">native | container — the blueprint's supervision kind, lower-cased.</param>
/// <param name="SteamAppId">Dedicated-server Steam App ID, or <c>null</c> for a non-Steam blueprint.</param>
/// <param name="ClientSteamAppId">Client Steam App ID for launch/connect deeplinks, or <c>null</c>.</param>
/// <param name="IsSteamAccountRequired">Whether a Steam account is required to download the server.</param>
/// <param name="Ports">The blueprint's declared default ports, structured (parsed at the kgsm-lib
/// chokepoint from the legacy UFW spec string — the API never re-parses an opaque port string).
/// Empty when the blueprint declares none.</param>
/// <param name="Specs">Advisory game specs from blueprint metadata (all <c>null</c> today).</param>
/// <param name="Cover">Resolved cover-art URL — RESERVED, always <c>null</c> at M8·a (see remarks).</param>
/// <param name="RawgSlug">The backend's RAWG lookup hint (<c>§3·i</c>) — RESERVED, always <c>null</c>
/// (no curated <c>rawg_slug</c> on the blueprint yet); the SPA ignores it.</param>
public sealed record LibraryEntry(
    string Id,
    string Name,
    string Type,
    string? SteamAppId,
    string? ClientSteamAppId,
    bool IsSteamAccountRequired,
    IReadOnlyList<LibraryPort> Ports,
    LibrarySpecs Specs,
    string? Cover,
    string? RawgSlug);

/// <summary>
/// One contiguous default port range a blueprint declares, structured (the canonical
/// <c>{ start, end, protocol }</c> shape — a single port has <c>start == end</c>). The blueprint
/// surface emits ports only as the legacy UFW string; kgsm-lib's <c>FromUfwSpec</c> parses it at the
/// chokepoint so the catalog carries structure, not an opaque string the SPA would have to split.
/// </summary>
/// <param name="Start">First port of the inclusive range.</param>
/// <param name="End">Last port of the inclusive range (== <paramref name="Start"/> for a single port).</param>
/// <param name="Proto">Transport protocol — <c>"tcp"</c> or <c>"udp"</c>, lower-cased.</param>
public sealed record LibraryPort(int Start, int End, string Proto);

/// <summary>
/// Advisory, vendor-declared game specs (<c>architecture.html §3·h "specs"</c>) — mapped 1:1 from
/// kgsm-lib's <c>BlueprintMetadata</c>. Every field is nullable: <c>null</c> means
/// <em>unknown/unbounded</em>, never a substitute for a real <c>0</c> (the never-fabricate-a-metric
/// invariant). All <c>null</c> today across every blueprint (metadata curation is deferred upstream);
/// the keys are present so the shape is stable as curation lands.
/// </summary>
/// <param name="MaxPlayers">Maximum players, or <c>null</c> if unbounded/configurable/unknown.</param>
/// <param name="MinRamMb">Advisory minimum RAM (MB), or <c>null</c>.</param>
/// <param name="RecommendedRamMb">Advisory recommended RAM (MB), or <c>null</c>.</param>
/// <param name="BaseDiskMb">Advisory base install footprint (MB), or <c>null</c>.</param>
public sealed record LibrarySpecs(
    int? MaxPlayers,
    int? MinRamMb,
    int? RecommendedRamMb,
    int? BaseDiskMb);
