using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// The slice of a RAWG.io <c>GET /api/games/{slug}</c> response the library needs (one call per game gives
/// everything). Plain reflection STJ (the API is JIT, not AOT) — the snake_case wire keys are mapped via
/// <see cref="JsonPropertyNameAttribute"/> rather than a global naming policy so this binds regardless of the
/// shared camelCase <see cref="Json.ApiJson"/> options.
/// </summary>
/// <remarks>
/// Image URLs are on <c>media.rawg.io</c> and are <c>.jpg</c>. A missing/unknown slug → RAWG 404 (the client
/// returns null); an unreleased game may have null images. We never re-encode the bytes (keep <c>.jpg</c>).
/// </remarks>
public sealed class RawgGame
{
    /// <summary>The cover image (grid tiles/cards), or null. RAWG <c>background_image</c>.</summary>
    [JsonPropertyName("background_image")]
    public string? BackgroundImage { get; set; }

    /// <summary>A hero screenshot (detail pages), FREE in the same response, or null.
    /// RAWG <c>background_image_additional</c>.</summary>
    [JsonPropertyName("background_image_additional")]
    public string? BackgroundImageAdditional { get; set; }

    /// <summary>Plain-text description (long, may carry <c>###</c> headers), or null. RAWG <c>description_raw</c>.</summary>
    [JsonPropertyName("description_raw")]
    public string? DescriptionRaw { get; set; }

    /// <summary>The game's genres. RAWG <c>genres[].name</c>.</summary>
    [JsonPropertyName("genres")]
    public List<RawgNamed>? Genres { get; set; }

    /// <summary>The game's tags (we keep the top ~8–12). RAWG <c>tags[].name</c>.</summary>
    [JsonPropertyName("tags")]
    public List<RawgNamed>? Tags { get; set; }

    /// <summary>Release date string, or null. RAWG <c>released</c>.</summary>
    [JsonPropertyName("released")]
    public string? Released { get; set; }

    /// <summary>Aggregate rating, or null. RAWG <c>rating</c>.</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    /// <summary>Official website, or null. RAWG <c>website</c>.</summary>
    [JsonPropertyName("website")]
    public string? Website { get; set; }
}

/// <summary>A RAWG named entity (a genre or a tag) — only the <c>name</c> is consumed.</summary>
public sealed class RawgNamed
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
