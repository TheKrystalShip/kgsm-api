using TheKrystalShip.Api.Services.Leaves;

namespace Api.Tests;

/// <summary>
/// Deterministic tests for the <see cref="SystemdReader"/> parser (<c>MergeShowOutput</c>) — the risky bit
/// that maps real <c>systemctl show --timestamp=unix</c> output onto <see cref="UnitState"/>. The sample is
/// verbatim from a live host (active/static-idle/not-found units), so the parser is pinned to reality.
/// </summary>
public class SystemdReaderTests
{
    // As emitted by `systemctl show <units> --timestamp=unix --property=Id,LoadState,ActiveState,SubState,
    // UnitFileState,MainPID,MemoryCurrent,ActiveEnterTimestamp` — blank-line separated blocks.
    private const string Sample =
        "Id=kgsm-api.service\n" +
        "LoadState=loaded\n" +
        "ActiveState=active\n" +
        "SubState=running\n" +
        "UnitFileState=enabled\n" +
        "ActiveEnterTimestamp=@1782763729\n" +
        "MainPID=3271183\n" +
        "MemoryCurrent=135106560\n" +
        "\n" +
        "Id=kgsm-firewall.service\n" +
        "LoadState=loaded\n" +
        "ActiveState=inactive\n" +
        "SubState=dead\n" +
        "UnitFileState=static\n" +
        "ActiveEnterTimestamp=\n" +
        "MainPID=0\n" +
        "MemoryCurrent=[not set]\n" +
        "\n" +
        "Id=does-not-exist.service\n" +
        "LoadState=not-found\n" +
        "ActiveState=inactive\n" +
        "SubState=dead\n" +
        "UnitFileState=\n" +
        "ActiveEnterTimestamp=\n" +
        "MainPID=0\n" +
        "MemoryCurrent=[not set]\n";

    private static Dictionary<string, UnitState> Seed(params string[] units)
    {
        var d = new Dictionary<string, UnitState>(StringComparer.Ordinal);
        foreach (string u in units) d[u] = UnitState.Unknown;
        return d;
    }

    [Fact]
    public void ActiveUnit_MapsLivenessFields()
    {
        var into = Seed("kgsm-api.service", "kgsm-firewall.service", "does-not-exist.service");
        SystemdReader.MergeShowOutput(Sample, into);

        UnitState api = into["kgsm-api.service"];
        Assert.Equal("active", api.State);
        Assert.Equal("running", api.SubState);
        Assert.True(api.Enabled);
        Assert.Equal(3271183, api.MainPid);
        Assert.Equal(135106560L, api.MemoryBytes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1782763729), api.Since);
    }

    [Fact]
    public void IdleStaticUnit_IsHonestNull_NotZero()
    {
        var into = Seed("kgsm-firewall.service");
        SystemdReader.MergeShowOutput(Sample, into);

        UnitState fw = into["kgsm-firewall.service"];
        Assert.Equal("inactive", fw.State);
        Assert.Equal("dead", fw.SubState);
        Assert.Null(fw.Enabled);       // static -> enablement N/A, not false
        Assert.Null(fw.Since);         // never active -> null, never epoch-zero
        Assert.Null(fw.MainPid);       // MainPID=0 -> null, never a fake pid
        Assert.Null(fw.MemoryBytes);   // "[not set]" -> null, never a fabricated 0
    }

    [Fact]
    public void NotFoundUnit_IsNotInstalled()
    {
        var into = Seed("does-not-exist.service");
        SystemdReader.MergeShowOutput(Sample, into);

        UnitState x = into["does-not-exist.service"];
        Assert.Equal("not-installed", x.State);
        Assert.Null(x.Enabled);
        Assert.Null(x.MainPid);
    }

    [Fact]
    public void RequestedUnit_AbsentFromOutput_StaysUnknown()
    {
        // A unit we asked for but systemctl never reported keeps the honest Unknown default (never invented).
        var into = Seed("kgsm-api.service", "kgsm-monitor.service");
        SystemdReader.MergeShowOutput(Sample, into);

        Assert.Equal("active", into["kgsm-api.service"].State);
        Assert.Equal("unknown", into["kgsm-monitor.service"].State);
    }

    [Fact]
    public void UnrequestedBlock_IsIgnored()
    {
        // A block whose Id wasn't in the seed map is dropped (we never add units we didn't ask for).
        var into = Seed("kgsm-firewall.service");
        SystemdReader.MergeShowOutput(Sample, into);

        Assert.False(into.ContainsKey("kgsm-api.service"));
        Assert.Single(into);
    }
}
