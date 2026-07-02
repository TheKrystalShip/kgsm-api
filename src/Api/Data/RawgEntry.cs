namespace TheKrystalShip.Api.Data;

/// <summary>
/// One blueprint's cached RAWG.io metadata (cover art + a hero screenshot + a cleaned description +
/// genres/tags), keyed by <see cref="BlueprintId"/>. The API hydrates this <b>once and refreshes ~30-day</b>
/// from RAWG (the <see cref="Services.Library.LibraryHydrationWorker"/>); after that, runtime never touches RAWG
/// (the key is refresh-only). The image bytes are self-hosted on disk (never hotlinked from media.rawg.io);
/// this row records where they landed + their ETags. The API serves them from its own
/// <c>GET /library/{id}/cover</c> / <c>/hero</c> endpoints.
/// <para>
/// <b>Identity vs payload (locked):</b> the slug lives in the kgsm blueprint (<c>metadata.rawg_slug</c>) — the
/// stateless engine owns the external-catalog id, like a Steam App ID. The fetched payload (cover bytes,
/// description, tags) lives here, in the consumer (kgsm-api). The engine never calls RAWG, never holds this cache.
/// </para>
/// <para>
/// ⚠ <b>EnsureCreated, NOT a migration</b> (the project's dev authority — see <see cref="AppDbContext"/>):
/// because <c>EnsureCreated</c> no-ops on an existing DB, adding this table means the dev DB file must be
/// deleted once. Smoke <c>rm -f</c>s its own DB and tests use a fresh temp DB; the deployed
/// <c>/var/lib/kgsm-api/kgsm-api.db</c> needs the one-time wipe.
/// </para>
/// <para>
/// <b>Never fabricate (honest):</b> a RAWG 404 / network failure / a game with no images leaves a sparse row
/// (the worker records <see cref="Status"/> and never wipes a previously-good row); a null slug never produces
/// a row at all. The <see cref="Genres"/>/<see cref="Tags"/> JSON columns hold an empty array (never null) when
/// the game has none; a missing <see cref="CoverFile"/>/<see cref="HeroFile"/> means the image is genuinely absent.
/// </para>
/// </summary>
public sealed class RawgEntry
{
    /// <summary>The kgsm blueprint id this metadata is for — the primary key (one row per blueprint).</summary>
    public string BlueprintId { get; set; } = "";

    /// <summary>The RAWG slug that was resolved (<c>metadata.rawg_slug</c> at fetch time) — recorded so a
    /// later slug change in the blueprint can be detected and re-fetched.</summary>
    public string Slug { get; set; } = "";

    /// <summary>The cleaned/truncated RAWG description (<c>description_raw</c> → lead paragraph), or null when
    /// RAWG had none. NB the precedence chain at serve time prefers a curated blueprint description over this.</summary>
    public string? Description { get; set; }

    /// <summary>RAWG genres (<c>genres[].name</c>) as a JSON array string (<c>["Action","Survival"]</c>);
    /// <c>"[]"</c> when none. (De)serialized at the store boundary into a <c>string[]</c>.</summary>
    public string? Genres { get; set; }

    /// <summary>RAWG tags (<c>tags[].name</c>, top ~8–12) as a JSON array string; <c>"[]"</c> when none.</summary>
    public string? Tags { get; set; }

    /// <summary>The on-disk cover-art file name (<c>{id}_cover.jpg</c> under the cache dir), or null if the
    /// cover bytes never landed. The cover is sourced <b>Steam-first</b> (the <c>library_600x900.jpg</c> capsule
    /// keyed by the blueprint's <c>client_steam_app_id</c>) with RAWG <c>background_image</c> as the fallback; this
    /// file name is the same regardless of which source supplied it (the source is logged, not persisted). The
    /// PRESENCE of this value is the "we have a cover" signal the aggregator builds the absolute serving URL from.</summary>
    public string? CoverFile { get; set; }

    /// <summary>The on-disk hero (screenshot) file name (<c>{id}_hero.jpg</c>), or null if none landed.</summary>
    public string? HeroFile { get; set; }

    /// <summary>The cover image's ETag (a content hash) for conditional-GET serving, or null.</summary>
    public string? CoverEtag { get; set; }

    /// <summary>The hero image's ETag, or null.</summary>
    public string? HeroEtag { get; set; }

    /// <summary>RAWG release date string (<c>released</c>), or null — informational, not on the frozen contract.</summary>
    public string? Released { get; set; }

    /// <summary>RAWG aggregate rating (<c>rating</c>), or null — informational, not on the frozen contract.</summary>
    public double? Rating { get; set; }

    /// <summary>RAWG official website (<c>website</c>), or null — informational, not on the frozen contract.</summary>
    public string? Website { get; set; }

    /// <summary>When this row was last (re)fetched from RAWG (UTC). Drives the ~30-day refresh window.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The last fetch outcome — honest provenance, never a fabricated success:
    /// <c>ok</c> (RAWG metadata fetched), <c>not_found</c> (RAWG was queried and 404'd/failed), or
    /// <c>cover_only</c> (a Steam-only hydration — no RAWG key or no slug, so only the cover landed; a later
    /// RAWG key triggers a refresh to fill the rest). The cover may still be present on a <c>not_found</c>/
    /// <c>cover_only</c> row when Steam supplied it.</summary>
    public string Status { get; set; } = "";
}
