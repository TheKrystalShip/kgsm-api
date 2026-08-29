using System.Text.Json;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// A leaf's floor: what its own configuration sets, with this API's override layer deliberately excluded.
/// <see cref="Complete"/> is false when a declared source could not be read — then a key absent from
/// <see cref="Values"/> is genuinely <em>unknown</em>, not "unset, so the default applies".
/// </summary>
public sealed record LeafFloor(IReadOnlyDictionary<string, string> Values, bool Complete)
{
    public static readonly LeafFloor Unknown = new(new Dictionary<string, string>(StringComparer.Ordinal), false);
}

/// <summary>
/// Reads the <c>floorSources</c> a leaf's descriptor declares, in order (lowest precedence first, matching
/// how the leaf itself resolves them), into a flat env-name → value map.
/// </summary>
/// <remarks>
/// <para><b>This API's own override layer is excluded.</b> That layer is the override provenance tier; folding
/// it into the floor would make every overridden key report as if the leaf had been configured that way by
/// hand. The exclusion is by path — the renderer's own output file — so it holds however the drop-in that
/// loads it is named.</para>
/// <para><b>Read-only, and honest about failure.</b> An unreadable source is never treated as an empty one:
/// "the file is not there" and "I could not open it" are different facts, and only the first one licenses
/// falling through to the descriptor default.</para>
/// </remarks>
public sealed class LeafFloorReader(
    ApiOptions options,
    LeafOverrideRenderer renderer,
    ILogger<LeafFloorReader> logger)
{
    public LeafFloor Read(LeafConfigDescriptor descriptor)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool complete = true;
        string overridePath = renderer.PathFor(descriptor.Id);

        foreach (LeafFloorSource source in descriptor.FloorSources)
        {
            try
            {
                switch (source.Kind)
                {
                    case "systemd-unit":
                        if (!ReadUnit(descriptor.Unit, source.Path, overridePath, values))
                            complete = false;
                        break;
                    case "env-file":
                        if (!ReadEnvFile(source.Path, values, required: false))
                            complete = false;
                        break;
                    case "appsettings":
                        if (!ReadAppSettings(source.Path, values))
                            complete = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "could not read floor source {Path} for {Leaf}", source.Path, descriptor.Id);
                complete = false;
            }
        }

        return new LeafFloor(values, complete);
    }

    // A unit's Environment= assignments plus every EnvironmentFile= it pulls in, drop-ins included and
    // applied in systemd's own order (the unit first, then drop-ins by filename). Returns false if the unit
    // itself is unreadable — a missing optional EnvironmentFile is not a failure, systemd tolerates it too.
    //
    // The unit is located the way systemd locates it, by NAME across every unit-file root, because where
    // the file sits is a property of how the host was provisioned rather than of the leaf: a package
    // leaves it in /usr/lib, a deploy script in /etc. A descriptor that names an absolute path instead
    // of a unit is honoured as a fallback, for a leaf keeping its unit somewhere no root covers.
    private bool ReadUnit(string unitName, string declared, string overridePath, Dictionary<string, string> into)
    {
        string? unitPath = SystemdUnitPaths.Fragment(unitName, options.LeafDropInDir);

        if (unitPath is null && Path.IsPathRooted(declared) && File.Exists(declared))
            unitPath = declared;

        if (unitPath is null)
            return false;

        var files = new List<string> { unitPath };
        files.AddRange(SystemdUnitPaths.DropIns(unitName, options.LeafDropInDir));

        foreach (string file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "could not read unit fragment {File}", file);
                return false;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';')
                    continue;

                if (line.StartsWith("Environment=", StringComparison.Ordinal))
                {
                    foreach ((string k, string v) in SplitAssignments(line["Environment=".Length..]))
                        into[k] = v;
                }
                else if (line.StartsWith("EnvironmentFile=", StringComparison.Ordinal))
                {
                    string path = line["EnvironmentFile=".Length..].Trim();
                    bool optional = path.StartsWith('-');
                    if (optional) path = path[1..].Trim();
                    path = Unquote(path);

                    // The API's own override layer is the override tier, not the floor.
                    if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(overridePath), StringComparison.Ordinal))
                        continue;

                    ReadEnvFile(path, into, required: !optional);
                }
            }
        }

        return true;
    }

    // systemd allows several assignments on one Environment= line, each optionally quoted.
    private static IEnumerable<(string Key, string Value)> SplitAssignments(string rest)
    {
        foreach (string token in Tokenize(rest))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0)
                continue;
            yield return (token[..eq], Unquote(token[(eq + 1)..]));
        }
    }

    // Split on whitespace, honouring single/double quotes so a quoted value containing a space stays whole.
    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';

        foreach (char c in s)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    // NAME=VALUE lines. An absent file is only a failure when the unit declared it non-optional.
    private bool ReadEnvFile(string path, Dictionary<string, string> into, bool required)
    {
        if (!File.Exists(path))
            return !required;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "could not read env file {Path}", path);
            return false;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            into[line[..eq].Trim()] = Unquote(line[(eq + 1)..].Trim());
        }

        return true;
    }

    // appsettings.json flattened the way IConfiguration maps env vars: Section__Key.
    private bool ReadAppSettings(string path, Dictionary<string, string> into)
    {
        if (!File.Exists(path))
            return true;   // a leaf may genuinely ship without one

        try
        {
            // Comments and trailing commas, because Microsoft.Extensions.Configuration's own JSON provider
            // accepts them — a leaf whose settings file is annotated is reading it fine, and rejecting it
            // here would report that leaf's whole floor as unknown over punctuation.
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            Flatten(doc.RootElement, prefix: "", into);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "could not read settings file {Path}", path);
            return false;
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty p in element.EnumerateObject())
                    Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "__" + p.Name, into);
                break;

            case JsonValueKind.Array:
                int i = 0;
                foreach (JsonElement item in element.EnumerateArray())
                    Flatten(item, prefix + "__" + i++, into);
                break;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                break;

            // A JSON boolean, spelled the way the descriptor and every other tier spell one. Left to
            // JsonElement.ToString() it arrives as "True", which is not a value any leaf's parser writes
            // and not what this field's default says — so the panel compares a floor of "True" against a
            // default of "true", finds them different, and renders a switch that is ON as off. That is the
            // panel misreporting what a leaf is running with, which is the one thing it exists not to do.
            case JsonValueKind.True or JsonValueKind.False:
                if (prefix.Length > 0)
                    into[prefix] = element.ValueKind == JsonValueKind.True ? "true" : "false";
                break;

            default:
                if (prefix.Length > 0)
                    into[prefix] = element.ToString();
                break;
        }
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            return v[1..^1];
        return v;
    }
}
