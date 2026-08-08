using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>One option of a command, named and typed the way the surface it runs on presents it.</summary>
/// <param name="Values">
/// The fixed set an <paramref name="Autocomplete"/> option offers, or null when it takes free text.
/// A Discord option is always free text here — its suggestions come from the bot as someone types,
/// not from the manifest.
/// </param>
public sealed record LeafCommandOption(
    string Name,
    string? Description,
    string Type,
    bool Required,
    bool Autocomplete,
    IReadOnlyList<string>? Values = null);

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
/// <param name="Surface">Where the commands are typed — <c>discord</c> for the bot, <c>chat</c> for the assistant.</param>
/// <param name="Gates">
/// The catalog, keyed by what the leaf itself requires of whoever runs the commands in that bucket — a
/// tier from the shared role map, or <c>none</c> when the leaf checks nothing. The leaf's own word for
/// its own check; this API neither interprets nor enforces it, and passes it through so the panel can
/// state who can act without guessing.
/// </param>
public sealed record LeafCommandManifest(
    int SchemaVersion,
    string Leaf,
    string Surface,
    IReadOnlyDictionary<string, IReadOnlyList<LeafCommand>> Gates)
{
    /// <summary>
    /// The shape this API serves, whatever version the file on disk was written as. A v1 file is
    /// restated into it on the way in (see <see cref="LeafCommandStore"/>), so a client reads one shape
    /// and never branches on where a manifest came from.
    /// </summary>
    public const int WireSchemaVersion = 2;

    /// <summary>
    /// The schema versions this API can read. Anything else is skipped, not guessed at — an unknown
    /// version means the rest of the file may mean something entirely different.
    /// </summary>
    public static readonly IReadOnlySet<int> SupportedSchemaVersions = new HashSet<int> { 1, 2 };

    /// <summary>The gate a leaf states when it checks nothing itself.</summary>
    public const string NoGate = "none";
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

    /// <summary>
    /// A manifest as it may appear on disk, in either schema version. Version 1 carries one leaf-wide
    /// <see cref="Gate"/> beside a flat <see cref="Commands"/> list; version 2 keys the catalog by the
    /// gate that admits each command. Both sets of fields are optional here so one read handles either,
    /// and <see cref="LeafCommandStore.Normalize"/> decides which was meant.
    /// </summary>
    private sealed record ManifestFile(
        int SchemaVersion,
        string? Leaf,
        string? Surface,
        string? Gate,
        IReadOnlyList<LeafCommand>? Commands,
        IReadOnlyDictionary<string, IReadOnlyList<LeafCommand>>? Gates);

    private LeafCommandManifest? TryRead(string file, string stem, out string? error)
    {
        error = null;
        ManifestFile? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(file), Json);
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
        if (!LeafCommandManifest.SupportedSchemaVersions.Contains(manifest.SchemaVersion))
        {
            error = $"schemaVersion {manifest.SchemaVersion} is not supported "
                  + $"(this API understands {string.Join(", ", LeafCommandManifest.SupportedSchemaVersions.Order())})";
            return null;
        }

        // The filename is how the leaf is addressed on the wire; a manifest whose leaf id disagrees would be
        // reachable under one name and describe itself as another.
        if (!string.Equals(manifest.Leaf, stem, StringComparison.Ordinal))
        {
            error = $"declares leaf '{manifest.Leaf ?? "(none)"}' but is installed as '{stem}.json'";
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.Surface))
        {
            error = "declares no surface";
            return null;
        }

        return Normalize(manifest, out error);
    }

    /// <summary>
    /// Restates a file of either version as the one shape this API serves, so a client never branches on
    /// where a manifest came from.
    /// <para>
    /// A version 1 file states one gate, and that gate is by definition what the leaf requires for a
    /// <see cref="LeafCommand.Mutates"/> command — so its acting commands go under it and its reading
    /// commands under <see cref="LeafCommandManifest.NoGate"/>, which is what "the leaf states no check
    /// for these" already meant. That is a restatement of what the file says, not a judgement about it:
    /// this API cannot verify a gate it does not implement, so it must not invent one either.
    /// </para>
    /// </summary>
    private static LeafCommandManifest? Normalize(ManifestFile manifest, out string? error)
    {
        error = null;

        Dictionary<string, IReadOnlyList<LeafCommand>> gates;
        if (manifest.Gates is not null)
        {
            gates = manifest.Gates.ToDictionary(g => g.Key, g => g.Value, StringComparer.Ordinal);
        }
        else if (manifest.Commands is not null)
        {
            if (string.IsNullOrWhiteSpace(manifest.Gate))
            {
                error = "states no gate for its commands";
                return null;
            }

            gates = manifest.Commands
                .GroupBy(c => c.Mutates ? manifest.Gate! : LeafCommandManifest.NoGate, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<LeafCommand>)[.. g], StringComparer.Ordinal);
        }
        else
        {
            error = "carries no commands";
            return null;
        }

        if (gates.Values.Any(bucket => bucket is null))
        {
            error = "a gate carries no command list";
            return null;
        }

        if (gates.Values.SelectMany(b => b).Any(c => string.IsNullOrWhiteSpace(c.Name)))
        {
            error = "a command has no name";
            return null;
        }

        return new LeafCommandManifest(
            LeafCommandManifest.WireSchemaVersion,
            manifest.Leaf!,
            manifest.Surface!,
            // A command that declares no options at all arrives with a null list; the wire says "takes no
            // options", which is what an absent list means, rather than passing the null on to the panel.
            gates.ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LeafCommand>)[.. g.Value.Select(c => c.Options is null ? c with { Options = [] } : c)],
                StringComparer.Ordinal));
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
