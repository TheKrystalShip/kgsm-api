namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Where systemd finds a unit's fragment and its drop-ins on this host.
/// </summary>
/// <remarks>
/// <para><b>systemd reads several roots, and so must anything reasoning about a unit.</b> A unit
/// installed by a package lives in <c>/usr/lib/systemd/system</c>; one an administrator wrote or a
/// deploy script placed lives in <c>/etc/systemd/system</c>; a generator's lives under
/// <c>/run/systemd/system</c>. Searching one of them answers correctly for hosts provisioned that one
/// way and wrongly for every other, which is the difference between a configuration page and a page
/// that says every leaf is unwired.</para>
/// <para><b>Drop-ins merge across roots; fragments do not.</b> systemd applies every <c>&lt;unit&gt;.d/*.conf</c>
/// it finds in any root, ordered by filename, so a drop-in shipped in <c>/usr/lib</c> is applied even
/// on a host with no <c>/etc</c> directory for that unit. A fragment, by contrast, is taken from the
/// highest-precedence root that has one and the rest are shadowed.</para>
/// <para><b>Nothing here spawns a process.</b> These are read on request paths, and the answer is a
/// directory listing.</para>
/// </remarks>
public static class SystemdUnitPaths
{
    /// <summary>
    /// The unit-file roots, highest precedence first — systemd's own order. <c>/usr/local/lib</c> sits
    /// where a locally built systemd package would install, above the distribution's own.
    /// </summary>
    public static readonly IReadOnlyList<string> StandardRoots =
    [
        "/etc/systemd/system",
        "/run/systemd/system",
        "/usr/local/lib/systemd/system",
        "/usr/lib/systemd/system",
    ];

    /// <summary>
    /// The roots to search: the one a host pinned, or the standard set. A pinned directory is searched
    /// alone — a host that names one is answering this question deliberately.
    /// </summary>
    public static IReadOnlyList<string> Roots(string? configuredDir) =>
        string.IsNullOrWhiteSpace(configuredDir) ? StandardRoots : [configuredDir.Trim()];

    /// <summary>
    /// The unit file systemd would read, or null when no root carries one. The first root wins, so
    /// an administrator's copy shadows the packaged one exactly as systemd has it.
    /// </summary>
    public static string? Fragment(string unit, string? configuredDir)
    {
        foreach (string root in Roots(configuredDir))
        {
            string candidate = Path.Combine(root, unit);
            if (Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Every drop-in applying to <paramref name="unit"/>, in the order systemd applies them: by
    /// filename across all roots, with a higher-precedence root's file shadowing a lower one of the
    /// same name. Empty when the unit has none.
    /// </summary>
    public static IReadOnlyList<string> DropIns(string unit, string? configuredDir)
    {
        // Keyed by filename so one root's 50-foo.conf replaces a lower root's, which is what systemd
        // does — the file is overridden, not applied twice.
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);

        // Walk lowest precedence first so a higher root overwrites what a lower one contributed.
        foreach (string root in Roots(configuredDir).Reverse())
        {
            string dir = Path.Combine(root, unit + ".d");
            foreach (string file in ListConfs(dir))
                byName[Path.GetFileName(file)] = file;
        }

        return [.. byName.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value)];
    }

    /// <summary>Whether a drop-in of this filename applies to the unit, in any root.</summary>
    public static bool HasDropIn(string unit, string dropInName, string? configuredDir) =>
        DropIns(unit, configuredDir)
            .Any(p => string.Equals(Path.GetFileName(p), dropInName, StringComparison.Ordinal));

    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static IEnumerable<string> ListConfs(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.conf") : [];
        }
        catch
        {
            // An unreadable drop-in directory is not an empty one, but there is nothing truthful to
            // report from here either — the caller's own "could not read" path covers the fragment,
            // which is the file that decides whether the unit is understood at all.
            return [];
        }
    }
}
