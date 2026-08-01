namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Reads the leaf config descriptors each leaf's own <c>deploy.sh</c> installs into
/// <see cref="ApiOptions.LeafDescriptorDir"/>. This API <strong>scans the directory</strong> — it holds no
/// list of leaves, so a leaf that joins the ecosystem later becomes configurable by landing one file, with
/// no rebuild here.
/// </summary>
/// <remarks>
/// <para><b>A bad descriptor is skipped, never fatal.</b> One leaf shipping a malformed file must not take
/// the config surface of every other leaf with it, and must never take the API down. The reason is logged
/// once per file revision (not once per rescan) so a persistent problem is visible without flooding the
/// journal.</para>
/// <para><b>Re-read on a TTL, not a file watcher.</b> Descriptors change only at deploy time, so a short
/// poll is enough and avoids an inotify watch that silently dies when a directory is replaced. The cost is
/// that a fresh deploy takes up to <see cref="TtlSeconds"/> to appear in the panel.</para>
/// </remarks>
public sealed class LeafDescriptorStore(ApiOptions options, ILogger<LeafDescriptorStore> logger)
{
    private const int TtlSeconds = 30;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, LeafConfigDescriptor> _byId =
        new Dictionary<string, LeafConfigDescriptor>(StringComparer.Ordinal);
    private DateTime _loadedUtc = DateTime.MinValue;

    /// <summary>Every descriptor currently installed on this host.</summary>
    public IReadOnlyCollection<LeafConfigDescriptor> All => Snapshot().Values.ToList();

    /// <summary>The descriptor for a leaf, or null when it has not shipped one.</summary>
    public LeafConfigDescriptor? For(string? leafId) =>
        leafId is not null && Snapshot().TryGetValue(leafId, out LeafConfigDescriptor? d) ? d : null;

    /// <summary>Drop the cache so the next read rescans. For tests and for an explicit refresh.</summary>
    public void Invalidate()
    {
        lock (_gate)
            _loadedUtc = DateTime.MinValue;
    }

    private IReadOnlyDictionary<string, LeafConfigDescriptor> Snapshot()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _loadedUtc).TotalSeconds < TtlSeconds)
                return _byId;

            _byId = Load();
            _loadedUtc = DateTime.UtcNow;
            return _byId;
        }
    }

    private Dictionary<string, LeafConfigDescriptor> Load()
    {
        var result = new Dictionary<string, LeafConfigDescriptor>(StringComparer.Ordinal);
        string dir = options.LeafDescriptorDir;

        string[] files;
        try
        {
            if (!Directory.Exists(dir))
                return result;   // no leaf has shipped a descriptor on this host yet — not an error
            files = Directory.GetFiles(dir, "*.json");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not list the leaf descriptor directory {Dir}", dir);
            return result;
        }

        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                ReportOnce(file, $"could not be read: {ex.Message}");
                continue;
            }

            LeafConfigDescriptor? descriptor = LeafConfigDescriptorParser.TryParse(json, out string? error);
            if (descriptor is null)
            {
                ReportOnce(file, error ?? "invalid");
                continue;
            }

            // The filename is how the leaf is addressed on the wire; a descriptor whose id disagrees would be
            // reachable under one name and describe itself as another.
            if (!string.Equals(descriptor.Id, stem, StringComparison.Ordinal))
            {
                ReportOnce(file, $"declares id '{descriptor.Id}' but is installed as '{stem}.json'");
                continue;
            }

            result[descriptor.Id] = descriptor;
        }

        return result;
    }

    // Log a bad descriptor once per revision of that file, so a permanent problem stays visible in the
    // journal without repeating every rescan. A fixed (or re-broken) file reports again.
    private void ReportOnce(string file, string reason)
    {
        string stamp;
        try
        {
            var info = new FileInfo(file);
            stamp = $"{file}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        }
        catch
        {
            stamp = file;
        }

        if (_reported.Add(stamp))
            logger.LogWarning("ignoring leaf descriptor {File}: {Reason}", file, reason);
    }
}
