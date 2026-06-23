using System.Globalization;
using System.Net;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Fetches a game's <b>Steam library capsule</b> — the 2:3 portrait cover art Steam shows in its library view
/// (<c>library_600x900.jpg</c>) — by its client Steam App ID. This is the library cover <b>authority</b>,
/// deliberately a <b>separate client from <see cref="IRawgClient"/></b> and fully decoupled from it: Steam needs
/// no API key, so it works regardless of whether RAWG is configured. RAWG is only the fallback (a game not on
/// Steam, or a Steam game with no capsule) and the authority for the OTHER metadata (hero/description/genres/tags).
/// Behind an interface so the hydration worker is unit-testable with a fake (no live HTTP).
/// </summary>
public interface ISteamCoverClient
{
    /// <summary>Whether the Steam cover source is active (<c>KGSM_API_STEAM_COVERS_DISABLED</c> off + a CDN base).
    /// When false the worker skips Steam and the cover falls back to RAWG.</summary>
    bool Enabled { get; }

    /// <summary>Download the Steam library capsule (<c>library_600x900.jpg</c>) for a client Steam App ID.
    /// Returns the raw <c>.jpg</c> bytes, or null when the app id is absent/zero/non-numeric, the capsule 404s
    /// (a non-Steam title / a server-only app id with no store page), or any failure — the caller then falls
    /// back to RAWG rather than fabricating. Never re-encodes the bytes.</summary>
    Task<byte[]?> DownloadCoverAsync(string? clientSteamAppId, CancellationToken ct);
}

/// <summary>
/// The live <see cref="ISteamCoverClient"/> — a typed <see cref="HttpClient"/> (registered via
/// <c>AddHttpClient&lt;ISteamCoverClient, SteamCoverClient&gt;()</c>). Unlike the RAWG client it carries
/// <b>no secret</b> in its URL (the appid is public), so its loggers are left intact. The capsule URL is
/// <c>{SteamCdnBaseUrl}/{appId}/library_600x900.jpg</c>.
/// </summary>
public sealed class SteamCoverClient(HttpClient http, ApiOptions options, ILogger<SteamCoverClient> logger) : ISteamCoverClient
{
    /// <summary>The Steam library-capsule asset (the canonical 2:3 portrait, 600×900).</summary>
    private const string CapsuleAsset = "library_600x900.jpg";

    public bool Enabled => options.SteamCoversProvisioned;

    public async Task<byte[]?> DownloadCoverAsync(string? clientSteamAppId, CancellationToken ct)
    {
        if (!Enabled || !TryParseAppId(clientSteamAppId, out long appId)) return null;

        string url = $"{options.SteamCdnBaseUrl}/{appId.ToString(CultureInfo.InvariantCulture)}/{CapsuleAsset}";
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // No capsule for this app id — a non-Steam title or a store-less (server-only) app id. Honest:
                // the worker falls back to RAWG's background_image; it never fabricates a cover.
                logger.LogDebug("Steam: no library capsule for appid {AppId} (404) — falling back to RAWG.", appId);
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Steam: capsule fetch for appid {AppId} failed with HTTP {Status}.", appId, (int)resp.StatusCode);
                return null;
            }

            // Guard against a 200 that is NOT an image (a CDN error/placeholder page) — writing that as a .jpg
            // would land a broken cover with no fallback path. Only accept an image/* content type.
            string? mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Steam: capsule for appid {AppId} returned non-image content-type '{Type}' — ignoring.",
                    appId, mediaType ?? "(none)");
                return null;
            }

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Steam: capsule download for appid {AppId} threw; falling back to RAWG.", appId);
            return null;
        }
    }

    /// <summary>A usable Steam App ID is a positive integer. The blueprint's <c>0</c>/blank "not Steam" sentinel
    /// (and any stray non-numeric value) yields false → no Steam fetch.</summary>
    internal static bool TryParseAppId(string? value, out long appId)
    {
        appId = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out appId) && appId > 0;
    }
}
