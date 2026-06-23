namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// The one place that pins the self-hosted image conventions so the writer (the hydration worker) and the
/// readers (the serving endpoint + the aggregator URL builder) can never drift:
/// <list type="bullet">
///   <item><description>the <b>on-disk file name</b> per blueprint/slot (<c>{id}_cover.jpg</c> / <c>{id}_hero.jpg</c>),</description></item>
///   <item><description>the <b>cache directory</b> (<c>KGSM_API_RAWG_CACHE_DIR</c>, default a <c>covers/</c> dir
///     beside the SQLite DB),</description></item>
///   <item><description>the <b>route segment</b> the aggregator builds an absolute URL from and the controller serves
///     (<c>cover</c> / <c>hero</c>).</description></item>
/// </list>
/// </summary>
public static class RawgCache
{
    /// <summary>The image slots — the route segment AND the on-disk suffix.</summary>
    public const string CoverSlot = "cover";
    public const string HeroSlot = "hero";

    /// <summary>The on-disk file name for a blueprint's image slot (<c>{id}_cover.jpg</c>). Always <c>.jpg</c>
    /// (we keep the RAWG source format, never re-encode).</summary>
    public static string FileName(string blueprintId, string slot) => $"{blueprintId}_{slot}.jpg";

    /// <summary>The absolute on-disk path for a blueprint's image slot under <paramref name="cacheDir"/>.</summary>
    public static string FilePath(string cacheDir, string blueprintId, string slot) =>
        Path.Combine(cacheDir, FileName(blueprintId, slot));
}
