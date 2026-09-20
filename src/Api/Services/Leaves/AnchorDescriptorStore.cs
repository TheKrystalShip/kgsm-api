using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Which components this host describes as <strong>anchors</strong> — the ids in
/// <see cref="ApiOptions.AnchorDescriptorDir"/>, where a component declaring <c>[Anchor]</c> installs its
/// descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids only, and deliberately.</b> An anchor is a peer of this node rather than something it hosts: it
/// owns its own configuration, serves its own journal and is reached at its own address. So this API reads
/// the directory for one fact — that a component is an anchor here — and reads nothing else out of it. The
/// one thing that fact decides is subtraction: a component described as an anchor is not one of this node's
/// services, so it is absent from the Services board and unaddressable as a leaf.
/// </para>
/// <para>
/// <b>Leaf-or-anchor is a deployment choice</b>, which is why this is measured per host rather than
/// compiled in. The same assistant is a node's leaf on one machine and the cluster's anchor on another, and
/// the descriptor it installed is the only thing that says which. A deploy clears the file it left in the
/// other directory, so the two directories cannot both claim it.
/// </para>
/// <para>
/// Re-read on the same TTL as <see cref="LeafDescriptorStore"/> and for the same reason: descriptors change
/// at deploy time, so a short poll beats an inotify watch that dies when a directory is replaced. An
/// unreadable directory or an unparseable file yields no id — an anchor this API cannot read about is one it
/// does not subtract, which leaves the component visible rather than silently erasing it.
/// </para>
/// </remarks>
public sealed class AnchorDescriptorStore(ApiOptions options, ILogger<AnchorDescriptorStore> logger)
{
    private const int TtlSeconds = 30;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private IReadOnlySet<string> _ids = new HashSet<string>(StringComparer.Ordinal);
    private DateTime _loadedUtc = DateTime.MinValue;

    /// <summary>The component ids described as anchors on this host. Empty when none is.</summary>
    public IReadOnlySet<string> Ids
    {
        get
        {
            lock (_gate)
            {
                if ((DateTime.UtcNow - _loadedUtc).TotalSeconds < TtlSeconds)
                    return _ids;

                _ids = Load();
                _loadedUtc = DateTime.UtcNow;
                return _ids;
            }
        }
    }

    /// <summary>Drop the cache so the next read rescans. For tests and for an explicit refresh.</summary>
    public void Invalidate()
    {
        lock (_gate)
            _loadedUtc = DateTime.MinValue;
    }

    private HashSet<string> Load()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string dir = options.AnchorDescriptorDir;

        string[] files;
        try
        {
            if (!Directory.Exists(dir))
                return result;   // no anchor is deployed on this host — not an error
            files = Directory.GetFiles(dir, "*.json");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not list the anchor descriptor directory {Dir}", dir);
            return result;
        }

        foreach (string file in files)
        {
            try
            {
                using FileStream stream = File.OpenRead(file);
                using JsonDocument doc = JsonDocument.Parse(stream);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } value)
                {
                    result.Add(value);
                }
                else
                {
                    ReportOnce(file, "declares no id");
                }
            }
            catch (Exception ex)
            {
                ReportOnce(file, $"could not be read: {ex.Message}");
            }
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
            logger.LogWarning("ignoring anchor descriptor {File}: {Reason}", file, reason);
    }
}
