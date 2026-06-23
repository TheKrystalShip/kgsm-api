using System.Net;
using System.Text.Json;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Fetches a game's metadata + the raw image bytes from RAWG.io for the library hydration worker. Behind an
/// interface so the worker is unit-testable with a fake (no live HTTP) — the project's seam-fake convention.
/// </summary>
public interface IRawgClient
{
    /// <summary>Whether RAWG hydration is enabled (a non-blank API key is configured). When false the worker
    /// no-ops — the opt-in switch: no key → null cover/hero, SPA's gradient fallback.</summary>
    bool Enabled { get; }

    /// <summary>Look up a game by slug. Returns the parsed game, or null on a 404 / network failure / disabled
    /// (honest-unknown — never a fabricated game). The key is appended internally and never logged.</summary>
    Task<RawgGame?> GetBySlugAsync(string slug, CancellationToken ct);

    /// <summary>Download an image's raw bytes (a <c>media.rawg.io</c> .jpg). Returns null on any failure — the
    /// caller then leaves the cover/hero absent rather than fabricating.</summary>
    Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct);
}

/// <summary>
/// The live <see cref="IRawgClient"/> — a typed <see cref="HttpClient"/> (registered via
/// <c>AddHttpClient&lt;IRawgClient, RawgClient&gt;()</c>). <b><see cref="HttpClient"/> has
/// <c>RemoveAllLoggers()</c></b> in Startup exactly like the Discord/Slack webhook providers: the request URL
/// carries <c>?key=…</c> (the API key IS a secret), and the default IHttpClientFactory logging handler would
/// otherwise write it to the app log on every request. Stripping the loggers keeps the key off the log channel.
/// </summary>
public sealed class RawgClient(HttpClient http, ApiOptions options, ILogger<RawgClient> logger) : IRawgClient
{
    private const string ApiBase = "https://api.rawg.io/api";

    // Reflection STJ (JIT, not AOT). The RawgGame model maps snake_case via [JsonPropertyName], so the only
    // option we need is case-insensitivity belt-and-braces; never re-encode anything.
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public bool Enabled => !string.IsNullOrWhiteSpace(options.RawgApiKey);

    public async Task<RawgGame?> GetBySlugAsync(string slug, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(slug)) return null;

        // Slug is curated/verified upstream; still encode it so a stray char can't break the path.
        string url = $"{ApiBase}/games/{Uri.EscapeDataString(slug)}?key={Uri.EscapeDataString(options.RawgApiKey)}";
        try
        {
            using HttpResponseMessage resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // A missing/unknown slug — honest: the worker records not_found and never wipes a good row.
                logger.LogInformation("RAWG: slug '{Slug}' not found (404).", slug);
                return null;
            }
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("RAWG: lookup for slug '{Slug}' failed with HTTP {Status}.", slug, (int)resp.StatusCode);
                return null;
            }
            await using Stream body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<RawgGame>(body, Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NB: never log the URL (it carries the key) — only the slug.
            logger.LogWarning(ex, "RAWG: lookup for slug '{Slug}' threw; leaving the cached row untouched.", slug);
            return null;
        }
    }

    public async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RAWG: image download failed ({Url}); leaving the image absent.", url);
            return null;
        }
    }
}
