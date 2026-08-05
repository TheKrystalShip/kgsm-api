using System.Globalization;

namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Reads a leaf's own memory charge from cgroup v2, given its main pid.
/// <para>
/// cgroup v2 counters are recursive and systemd's <c>MemoryCurrent</c> is the whole unit subtree, so a unit
/// that supervises other workloads in child cgroups reports their memory as its own. <c>kgsm-watchdog</c>
/// does exactly that — it runs itself in a <c>supervisor</c> child and spawns each game server into a
/// sibling — so its unit-level figure is dominated by the servers it supervises rather than by the daemon.
/// </para>
/// <para>
/// What this reads instead is <b>the memory charged to the cgroup the leaf's main process actually lives
/// in</b> (<c>/proc/&lt;pid&gt;/cgroup</c> → that directory's <c>memory.current</c>). That is still
/// recursive over <em>its</em> descendants, which is the right boundary: a leaf that forks work into
/// sub-cgroups keeps it counted, while a workload supervised in a sibling cgroup is excluded. For a leaf
/// whose main process sits directly in its unit cgroup — every leaf but the watchdog — it is the same
/// number systemd reports.
/// </para>
/// <para>
/// Both files are world-readable, so this needs no privilege. Anything unreadable is <c>null</c>: the pid
/// exiting mid-read is the ordinary case, and "not measured" is the honest answer. The unit-level figure is
/// never substituted — it is a different quantity, not a degraded version of this one, and putting it under
/// this field is the misattribution the reader exists to avoid.
/// </para>
/// <para>
/// Like <c>memory.current</c> everywhere else in the ecosystem this counts reclaimable page cache, so it
/// sits above the process's RSS.
/// </para>
/// </summary>
internal static class CgroupMemoryReader
{
    internal const string DefaultProcRoot = "/proc";
    internal const string DefaultCgroupRoot = "/sys/fs/cgroup";

    /// <summary>
    /// The memory charged to <paramref name="pid"/>'s own cgroup, or <c>null</c> when it can't be measured
    /// (no unified cgroup line, the process is gone, the file is unreadable, or the pid sits in the root
    /// cgroup — which exposes no <c>memory.current</c>). The roots are parameters so the pure path logic can
    /// be tested against a fixture tree.
    /// </summary>
    public static long? TryRead(int pid, string procRoot = DefaultProcRoot, string cgroupRoot = DefaultCgroupRoot)
    {
        if (pid <= 0)
            return null;

        if (!TryReadText(Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture), "cgroup"), out string cgroupFile))
            return null;

        string? relative = ParseUnifiedPath(cgroupFile);
        if (relative is null)
            return null;

        if (!TryReadText(Path.Combine(cgroupRoot, relative, "memory.current"), out string current))
            return null;

        return long.TryParse(current.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
            ? bytes
            : null;
    }

    /// <summary>
    /// The cgroup v2 path from a <c>/proc/&lt;pid&gt;/cgroup</c> body, as a path <em>relative</em> to the
    /// cgroup root (no leading slash — <see cref="Path.Combine(string, string)"/> would discard the root
    /// otherwise). Only the unified <c>0::</c> line counts: a v1 controller line addresses a different
    /// hierarchy whose numbers are not this one's. Returns <c>null</c> for a process in the root cgroup,
    /// which has no <c>memory.current</c> to read.
    /// </summary>
    internal static string? ParseUnifiedPath(string content)
    {
        foreach (string line in content.Split('\n'))
        {
            if (!line.StartsWith("0::", StringComparison.Ordinal))
                continue;
            string path = line[3..].TrimEnd('\r').Trim();
            return path.Length <= 1 ? null : path.TrimStart('/');   // "/" is the root cgroup
        }
        return null;
    }

    private static bool TryReadText(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch
        {
            // The process exited, the cgroup was torn down, or the host isn't cgroup v2 — all "not measured".
            content = string.Empty;
            return false;
        }
    }
}
