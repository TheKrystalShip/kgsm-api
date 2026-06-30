using System.Text;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Materializes a leaf's override rows into <c>&lt;LeafOverridesDir&gt;/&lt;leaf&gt;.env</c> — a deterministic
/// render of the <c>leaf_override</c> DB rows that a systemd drop-in feeds the leaf via
/// <c>EnvironmentFile=-</c> (the leaf-runtime-config feature). The DB is the source of truth; this file is a
/// pure render, regenerated on every change — so "reset to default" = delete the rows + re-render (the file
/// becomes absent). Unprivileged: the dir is the API's own state dir.
/// </summary>
/// <remarks>
/// <b>Atomic + locked-down.</b> Each render writes a temp file in the same dir (mode <c>0600</c> — the
/// overrides can hold a secret) then renames it over the target (atomic on one filesystem), so a leaf restart
/// never reads a half-written file. The dir is created <c>0700</c> if missing. A value's CR/LF are stripped
/// so an override can never inject a second env line. <b>Never logs a value</b> (secret hygiene).
/// </remarks>
public sealed class LeafOverrideRenderer(ApiOptions options, ILogger<LeafOverrideRenderer> logger)
{
    private const UnixFileMode FileMode0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode DirMode0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The override file path for a leaf (<c>&lt;LeafOverridesDir&gt;/&lt;leaf&gt;.env</c>).</summary>
    public string PathFor(string leafId) => Path.Combine(options.LeafOverridesDir, leafId + ".env");

    /// <summary>
    /// Render <paramref name="rows"/> to the leaf's override file. With no rows the file is deleted (the leaf
    /// falls back to its deploy-floor). Each row's value is written as <c>EnvName=value</c> using the manifest
    /// field's env name; a row whose key is not in the manifest is skipped (defensive — the broker only ever
    /// stores manifest keys).
    /// </summary>
    public void Render(string leafId, IReadOnlyList<LeafOverrideRow> rows)
    {
        string path = PathFor(leafId);

        if (rows.Count == 0)
        {
            // Reset-to-floor: no overrides ⇒ no file (the EnvironmentFile=- drop-in tolerates an absent file).
            TryDelete(path);
            return;
        }

        Directory.CreateDirectory(options.LeafOverridesDir);
        if (OperatingSystem.IsLinux())
        {
            try { File.SetUnixFileMode(options.LeafOverridesDir, DirMode0700); }
            catch (Exception ex) { logger.LogDebug(ex, "could not chmod 0700 the leaf overrides dir"); }
        }

        var sb = new StringBuilder();
        sb.Append("# Rendered by kgsm-api — do not edit (managed via the Services config panel).\n");
        foreach (LeafOverrideRow row in rows)
        {
            LeafConfigFieldDef? field = LeafConfigManifest.Field(leafId, row.Key);
            if (field is null) continue; // not a manifest key → never write it
            sb.Append(field.EnvName).Append('=').Append(SingleLine(row.Value)).Append('\n');
        }

        // Atomic: write a sibling temp at 0600, then rename over the target (same-dir rename is atomic).
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tmp, FileMode0600);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    // A value can never span lines (it would inject a second KEY=VALUE) — strip CR/LF. Other chars pass
    // through (log levels / ints / bools / api keys carry none of concern).
    private static string SingleLine(string? value) =>
        value is null ? "" : value.Replace("\r", "").Replace("\n", "");

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogDebug(ex, "could not delete a leaf override file"); }
    }
}
