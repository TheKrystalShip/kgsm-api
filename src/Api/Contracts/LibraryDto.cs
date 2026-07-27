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
///     (a blueprint that declares no <c>display_name</c> falls back to <c>name == id</c>).</description></item>
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
///   <item><description><c>specs</c> keys are always present but every value is nullable — a
///     <c>null</c> spec is "unknown/unbounded" for a field that blueprint doesn't declare, never a
///     fabricated 0. Coverage is per-blueprint (RAM is widely curated; <c>baseDiskMb</c> is
///     sparser).</description></item>
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
/// <param name="Specs">Advisory game specs from blueprint metadata; each field <c>null</c> where that
/// blueprint declares no value (unknown/unbounded), never fabricated.</param>
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
/// invariant). Coverage is per-blueprint — RAM (<c>MinRamMb</c>/<c>RecommendedRamMb</c>) is widely curated,
/// <c>BaseDiskMb</c> is sparser — and the keys are always present so the shape is stable regardless.
/// There is no CPU field: a single number can't honestly represent CPU capability, so it is deliberately
/// not a spec (capacity/placement reasons over RAM + disk only).
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

/// <summary>
/// A blueprint's raw file, as the library editor loads it — <c>GET /library/{id}/file</c>. Byte-level
/// text, never a typed round-trip: kgsm-lib's typed blueprint path handles native blueprints only and
/// drops every comment, so a container blueprint or a commented one could not survive being parsed and
/// re-rendered. What is read is exactly what is on disk.
/// </summary>
/// <param name="Name">The blueprint id (the <c>{id}</c> route segment), echoed back.</param>
/// <param name="Content">The file's exact text.</param>
/// <param name="Encoding">Always <c>utf-8</c> — a file whose bytes are not valid UTF-8 is refused rather
/// than transcoded (the same rule as the instance file browser).</param>
/// <param name="SizeBytes">The file's size on disk.</param>
/// <param name="Mtime">The file's last-modified time.</param>
/// <param name="Etag">An <c>sha256:&lt;hex&gt;</c> content identity, echoed back on save for optimistic
/// concurrency (a mismatch is a <c>412</c>).</param>
/// <param name="Tier">Which blueprints directory this file came from — <c>system</c> (shipped with the
/// engine) or <c>user</c> (kgsm's writable directory).</param>
/// <param name="OverridesSystem">A user file is shadowing a shipped one of the same name. Engine-sourced
/// from the blueprint's candidate paths, never inferred.</param>
/// <param name="CanRevert">Whether <c>DELETE /library/{id}/file</c> would restore a shipped original —
/// equal to <paramref name="OverridesSystem"/>, surfaced separately so a client never has to derive the
/// rule that reverting a blueprint with no original would destroy the only copy. The API refuses that
/// case independently (<c>409 no_original</c>) whether or not a client checks this.</param>
/// <param name="ReadOnly">Whether THIS caller may save. Writes are admin-only while reads are operator+,
/// so an operator gets the file with <c>readOnly: true</c> — the editor opens, the buttons do not.</param>
/// <param name="Runtime">The blueprint's runtime (<c>native</c>/<c>container</c>) as the engine reports it
/// in the catalog, or <c>null</c> when this blueprint isn't in the cached catalog (a brand-new one, or a
/// malformed file the engine won't enumerate — which is precisely a file worth opening to repair). Never
/// parsed out of <paramref name="Content"/> by this API.</param>
/// <param name="HostId">The host whose disk this file lives on. The catalog is a merged multi-host view
/// but a blueprint file is one host's, so an edit is never ambiguous about where it landed.</param>
public sealed record BlueprintFileDto(
    string Name,
    string Content,
    string Encoding,
    long SizeBytes,
    DateTimeOffset Mtime,
    string Etag,
    string Tier,
    bool OverridesSystem,
    bool CanRevert,
    bool ReadOnly,
    string? Runtime,
    string HostId);

/// <summary>Body of <c>PUT /library/{id}/file</c>.</summary>
/// <param name="Content">The exact file text to write. Required.</param>
/// <param name="Etag">The etag from the read this edit was based on. When set and no longer current, the
/// save is refused with <c>412</c> rather than clobbering someone else's change.</param>
/// <param name="Origin">The surface driving the write (<c>ui</c>|<c>assistant</c>|<c>discord</c>|<c>api</c>,
/// default <c>api</c>). Stamped onto the kgsm event so the audit row records where the edit came from.</param>
public sealed record SaveBlueprintRequest(string? Content, string? Etag, string? Origin);

/// <summary>Result of <c>PUT /library/{id}/file</c>.</summary>
/// <param name="Etag">The saved content's new identity — carry it into the next save.</param>
/// <param name="Tier">Always <c>user</c>: a write goes to kgsm's writable directory, always. The shipped
/// directory is the engine deploy's rsync target and is structurally unreachable from this surface.</param>
/// <param name="OverridesSystem">Whether the saved file is now shadowing a shipped blueprint.</param>
/// <param name="CreatedOverride">Whether THIS save is what started that shadowing. The client uses it to
/// explain, once, that the shipped blueprint is now overridden and will not receive upstream updates.</param>
public sealed record SaveBlueprintResultDto(
    string Etag,
    long SizeBytes,
    DateTimeOffset Mtime,
    string Tier,
    bool OverridesSystem,
    bool CreatedOverride);

/// <summary>Result of <c>DELETE /library/{id}/file</c> — the user-dir override is gone and the shipped
/// blueprint serves again.</summary>
/// <param name="RevertedTo">The tier now serving this blueprint; always <c>system</c>, since a revert is
/// only permitted when a shipped original exists.</param>
/// <param name="Etag">Identity of the now-effective shipped file, or <c>null</c> if it could not be
/// re-read — the revert still happened, so this is honestly empty rather than stale.</param>
public sealed record RevertBlueprintResultDto(
    string RevertedTo,
    string? Etag,
    long SizeBytes,
    DateTimeOffset Mtime);
