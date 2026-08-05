using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>One option of a command, named and typed the way the surface it runs on presents it.</summary>
public sealed record LeafCommandOption(
    string Name,
    string? Description,
    string Type,
    bool Required,
    bool Autocomplete);

/// <summary>
/// One command a person can type at a leaf. <see cref="Mutates"/> separates the commands that act on a
/// server from the ones that only read — the distinction an operator opens the list to find.
/// </summary>
public sealed record LeafCommand(
    string Name,
    string Description,
    bool Mutates,
    IReadOnlyList<LeafCommandOption> Options);

/// <summary>
/// A leaf's catalog of the commands it answers to, shipped as
/// <c>/var/lib/kgsm/leaves/commands/&lt;id&gt;.json</c> by that leaf's own <c>deploy.sh</c> and read (never
/// written) here.
/// </summary>
/// <param name="Surface">Where the commands are typed — <c>discord</c> for the bot.</param>
/// <param name="Gate">
/// What the leaf itself requires of whoever runs a <see cref="LeafCommand.Mutates"/> command. The leaf's
/// own word for its own check; this API neither interprets nor enforces it, and passes it through so the
/// panel can state who can act without guessing.
/// </param>
public sealed record LeafCommandManifest(
    int SchemaVersion,
    string Leaf,
    string Surface,
    string Gate,
    IReadOnlyList<LeafCommand> Commands)
{
    /// <summary>The only schema version this API understands; anything else is skipped, not guessed at.</summary>
    public const int SupportedSchemaVersion = 1;
}

/// <summary>
/// Reads the command manifests leaves install beside their config descriptors, in the
/// <c>commands/</c> subdirectory of <see cref="ApiOptions.LeafDescriptorDir"/>. As with descriptors this
/// API <strong>scans the directory</strong> and holds no list of leaves: a leaf that grows a command
/// surface becomes documented in the panel by landing one file, with no rebuild here.
/// </summary>
/// <remarks>
/// <para><b>Why a subdirectory.</b> The descriptor scan globs <c>*.json</c> at the top level, so a manifest
/// sitting there would be read as a malformed descriptor and logged as one on every deploy. One directory
/// down, the two kinds of file cannot be confused for each other.</para>
/// <para><b>Why a file at all.</b> The bot has no listening surface to ask, and the list is most wanted
/// when the unit is stopped — the same reasoning that makes the config descriptor a shipped file.</para>
/// <para>A malformed manifest is skipped with its reason logged once per file revision, never fatal, and
/// never partially applied — a half-read command list would tell an operator to type something that does
/// not exist.</para>
/// </remarks>
public sealed class LeafCommandStore(ApiOptions options, ILogger<LeafCommandStore> logger)
{
    private const int TtlSeconds = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Lock _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, LeafCommandManifest> _byId =
        new Dictionary<string, LeafCommandManifest>(StringComparer.Ordinal);
    private DateTime _loadedUtc = DateTime.MinValue;

    /// <summary>The directory manifests are installed into: <c>commands/</c> beside the descriptors.</summary>
    public string Directory => Path.Combine(options.LeafDescriptorDir, "commands");

    /// <summary>The command manifest for a leaf, or null when it ships none — most leaves take no commands.</summary>
    public LeafCommandManifest? For(string? leafId) =>
        leafId is not null && Snapshot().TryGetValue(leafId, out LeafCommandManifest? m) ? m : null;

    /// <summary>Drop the cache so the next read rescans. For tests and for an explicit refresh.</summary>
    public void Invalidate()
    {
        lock (_gate)
            _loadedUtc = DateTime.MinValue;
    }

    private IReadOnlyDictionary<string, LeafCommandManifest> Snapshot()
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

    private Dictionary<string, LeafCommandManifest> Load()
    {
        var result = new Dictionary<string, LeafCommandManifest>(StringComparer.Ordinal);
        string dir = Directory;

        string[] files;
        try
        {
            if (!System.IO.Directory.Exists(dir))
                return result;   // no leaf on this host takes commands — not an error
            files = System.IO.Directory.GetFiles(dir, "*.json");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not list the leaf command directory {Dir}", dir);
            return result;
        }

        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            LeafCommandManifest? manifest = TryRead(file, stem, out string? error);
            if (manifest is null)
                ReportOnce(file, error ?? "invalid");
            else
                result[manifest.Leaf] = manifest;
        }

        return result;
    }

    private LeafCommandManifest? TryRead(string file, string stem, out string? error)
    {
        error = null;
        LeafCommandManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LeafCommandManifest>(File.ReadAllText(file), Json);
        }
        catch (Exception ex)
        {
            error = ex is JsonException ? $"not valid JSON: {ex.Message}" : $"could not be read: {ex.Message}";
            return null;
        }

        if (manifest is null)
        {
            error = "empty document";
            return null;
        }

        // Version first: an unknown version means the rest of the file may mean something else entirely.
        if (manifest.SchemaVersion != LeafCommandManifest.SupportedSchemaVersion)
        {
            error = $"schemaVersion {manifest.SchemaVersion} is not supported "
                  + $"(this API understands {LeafCommandManifest.SupportedSchemaVersion})";
            return null;
        }

        // The filename is how the leaf is addressed on the wire; a manifest whose leaf id disagrees would be
        // reachable under one name and describe itself as another.
        if (!string.Equals(manifest.Leaf, stem, StringComparison.Ordinal))
        {
            error = $"declares leaf '{manifest.Leaf}' but is installed as '{stem}.json'";
            return null;
        }

        if (manifest.Commands is null || manifest.Commands.Any(c => string.IsNullOrWhiteSpace(c.Name)))
        {
            error = "a command has no name";
            return null;
        }

        // A command that declares no options at all arrives with a null list; the wire says "takes no
        // options", which is what an absent list means, rather than passing the null on to the panel.
        return manifest with
        {
            Commands = [.. manifest.Commands.Select(c => c.Options is null ? c with { Options = [] } : c)],
        };
    }

    // Log a bad manifest once per revision of that file, so a permanent problem stays visible in the journal
    // without repeating every rescan. A fixed (or re-broken) file reports again.
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
            logger.LogWarning("ignoring leaf command manifest {File}: {Reason}", file, reason);
    }
}
