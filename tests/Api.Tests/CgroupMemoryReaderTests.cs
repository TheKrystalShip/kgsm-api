using TheKrystalShip.Api.Services.Leaves;

namespace Api.Tests;

/// <summary>
/// Tests for <see cref="CgroupMemoryReader"/> against a fixture cgroup tree. The layout mirrors this host's
/// real one: <c>kgsm-watchdog</c> runs itself in a <c>supervisor</c> child of its unit cgroup and spawns each
/// game server into a sibling, so the unit's own recursive <c>memory.current</c> is dominated by the servers.
/// The numbers are the measured ones from that host, which is what makes the headline test mean something —
/// the daemon's real footprint is 56&#160;MB while its unit cgroup reads 8.9&#160;GB.
/// </summary>
public sealed class CgroupMemoryReaderTests : IDisposable
{
    private const long UnitSubtreeBytes = 8_924_176_384;   // the watchdog unit cgroup: daemon + both servers
    private const long SupervisorBytes = 56_336_384;       // the daemon itself
    private const long ServerBytes = 5_927_772_160;        // one supervised game server

    private readonly string _root = Directory.CreateTempSubdirectory("kgsm-cgroup-test-").FullName;

    private string ProcRoot => Path.Combine(_root, "proc");
    private string CgroupRoot => Path.Combine(_root, "cgroup");

    public CgroupMemoryReaderTests()
    {
        // The supervising unit: its own recursive total, the daemon's cgroup, and one server sibling.
        string unit = Path.Combine(CgroupRoot, "kgsm.slice", "kgsm-watchdog.service");
        WriteCgroup(unit, UnitSubtreeBytes);
        WriteCgroup(Path.Combine(unit, "supervisor"), SupervisorBytes);
        WriteCgroup(Path.Combine(unit, "Ketchup"), ServerBytes);
        WriteProc(621, "0::/kgsm.slice/kgsm-watchdog.service/supervisor");

        // An ordinary leaf: main process directly in its unit cgroup, no children.
        WriteCgroup(Path.Combine(CgroupRoot, "system.slice", "kgsm-bot.service"), 111_624_192);
        WriteProc(1849, "0::/system.slice/kgsm-bot.service");
    }

    private void WriteCgroup(string dir, long current)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "memory.current"), current + "\n");
    }

    private void WriteProc(int pid, string cgroupLine)
    {
        string dir = Path.Combine(ProcRoot, pid.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup"), cgroupLine + "\n");
    }

    private long? Read(int pid) => CgroupMemoryReader.TryRead(pid, ProcRoot, CgroupRoot);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SupervisingUnit_ReportsTheDaemon_NotItsSupervisedWorkloads()
    {
        // The whole point: the unit cgroup's figure is 158x the daemon's, because cgroup counters are
        // recursive and the servers are charged beneath it. Reading the main process's own cgroup skips them.
        Assert.Equal(SupervisorBytes, Read(621));
        Assert.NotEqual(UnitSubtreeBytes, Read(621));
        Assert.True(Read(621) < ServerBytes, "a supervised server must not be counted as the supervisor's memory");
    }

    [Fact]
    public void OrdinaryLeaf_MatchesItsUnitCgroup()
    {
        // A leaf whose main process sits directly in its unit cgroup — every leaf but the watchdog — reads
        // exactly what systemd would have reported. The fix changes nothing for them.
        Assert.Equal(111_624_192L, Read(1849));
    }

    [Fact]
    public void VanishedProcess_IsNull_NeverTheSubtreeTotal()
    {
        // The ordinary failure: the pid exited between the systemctl read and this one. "Not measured" is the
        // honest answer — falling back to the unit total would reinstate exactly the misattribution above.
        Assert.Null(Read(999999));
    }

    [Fact]
    public void UnreadableCgroup_IsNull()
    {
        // The process is there and names a cgroup, but the directory has no memory.current (torn down, or a
        // controller that isn't enabled).
        WriteProc(4242, "0::/system.slice/gone.service");
        Assert.Null(Read(4242));
    }

    [Fact]
    public void ZeroPid_IsNull()
    {
        // MainPID=0 already maps to a null pid upstream; guard it here too rather than statting /proc/0.
        Assert.Null(Read(0));
    }

    [Theory]
    // The unified line, with the leading slash stripped so it composes onto the cgroup root.
    [InlineData("0::/system.slice/kgsm-bot.service\n", "system.slice/kgsm-bot.service")]
    // Hybrid hosts list v1 controllers too; only the unified line addresses the hierarchy these counters live in.
    [InlineData("12:pids:/system.slice/x.service\n0::/system.slice/y.service\n", "system.slice/y.service")]
    [InlineData("0::/\n", null)]                                   // root cgroup — exposes no memory.current
    [InlineData("11:devices:/system.slice/x.service\n", null)]      // cgroup v1 only — no unified line to read
    [InlineData("", null)]
    public void ParseUnifiedPath_TakesOnlyTheUnifiedLine(string content, string? expected)
        => Assert.Equal(expected, CgroupMemoryReader.ParseUnifiedPath(content));
}
