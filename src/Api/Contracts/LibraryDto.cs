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
///   <item><description><c>cover</c>/<c>hero</c> are <strong>absolute, directly-renderable image URLs</strong>
///     (or null) pointing at this API's own <c>GET /library/{id}/cover</c> / <c>/hero</c> endpoints. They are
///     resolved server-side and self-hosted on disk. <c>cover</c> is the Steam library capsule (the 2:3
///     portrait) keyed off the blueprint's <c>client_steam_app_id</c>, falling back to RAWG's landscape
///     <c>background_image</c> (keyed off the curated <c>rawg_slug</c>, an exact id — never a fuzzy name match
///     that would mis-attribute the wrong game's art); <c>hero</c> is RAWG-only. With neither source available
///     they stay <c>null</c> (the SPA's gradient fallback).</description></item>
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
/// <param name="Cover">Absolute, directly-renderable cover-art URL — the Steam library capsule (2:3 portrait)
/// when the game is on Steam, else RAWG <c>background_image</c>; self-hosted at <c>GET /library/{id}/cover</c>,
/// or <c>null</c> when none is cached (no source / unresolved).</param>
/// <param name="Hero">Absolute, directly-renderable hero/screenshot URL (RAWG <c>background_image_additional</c>,
/// self-hosted at <c>GET /library/{id}/hero</c>), or <c>null</c> when none is cached.</param>
/// <param name="Description">A short blurb: the curated blueprint description, else the cleaned/truncated RAWG
/// description, else <c>null</c> (the precedence chain — never fabricated).</param>
/// <param name="Genres">RAWG genres (<c>genres[].name</c>); <c>[]</c> when none/unresolved.</param>
/// <param name="Tags">RAWG tags (<c>tags[].name</c>, top ~8–12); <c>[]</c> when none/unresolved.</param>
/// <param name="RawgSlug">The blueprint's curated RAWG lookup hint (<c>metadata.rawg_slug</c>), or <c>null</c>
/// when the blueprint declares none — the slug the backend resolves cover/metadata from.</param>
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
    string? Hero,
    string? Description,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    string? RawgSlug);

/// <summary>
/// One contiguous default port range a blueprint declares, structured (the canonical
/// <c>{ start, end, protocol }</c> shape — a single port has <c>start == end</c>). kgsm emits this
/// directly on <c>blueprints … --json</c> and kgsm-lib types it as <c>List&lt;PortMapping&gt;</c>, so
/// the catalog just projects it — no port-string parsing, not an opaque string the SPA would have to split.
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
