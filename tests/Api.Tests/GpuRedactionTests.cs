using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What the GPU process list looks like to a reader below operator.
/// </summary>
/// <remarks>
/// The monitor sees every compute context on the card, and a host runs things besides KGSM. Naming
/// somebody's training run to every viewer is the same mistake as putting a player's address on an
/// audit row — so a context belonging to no known unit loses its pid and its name.
/// <para>
/// The property under all of it: <b>withholding a name must not understate a total.</b> The withheld
/// memory stays, aggregated into one unnamed row per device, so a viewer still sees a full card and
/// something they cannot identify holding it. Dropping the rows would leave the per-process figures
/// failing to add up to the device's — a quieter falsehood than the one the gate exists to prevent.
/// </para>
/// </remarks>
public sealed class GpuRedactionTests
{
    private static GpuProcessSample Proc(int pid, string? unit, double mem, double? sm = null, int device = 0) =>
        new(device, pid, $"proc-{pid}", unit, mem, sm);

    [Fact]
    public void A_context_belonging_to_a_known_unit_is_untouched()
    {
        var kept = GpuRedaction.ForViewer([Proc(100, "kgsm-llama-chat.service", 8.5, 93)]);

        GpuProcessSample only = Assert.Single(kept!);
        Assert.Equal(100, only.Pid);
        Assert.Equal("proc-100", only.Name);
        Assert.Equal("kgsm-llama-chat.service", only.Unit);
        Assert.Equal(8.5, only.MemUsed);
        Assert.Equal(93, only.SmPct);
    }

    [Fact]
    public void An_unattributed_context_loses_its_identity_but_keeps_its_memory()
    {
        var kept = GpuRedaction.ForViewer([Proc(4242, null, 3.25)]);

        GpuProcessSample only = Assert.Single(kept!);
        Assert.Null(only.Pid);
        Assert.Null(only.Name);
        Assert.Equal(3.25, only.MemUsed);   // the card is still as full as it really is
    }

    [Fact]
    public void Several_unattributed_contexts_collapse_into_one_row_per_device()
    {
        // Two anonymous processes must not become two rows a viewer could count — the number of things
        // running is itself something the gate withholds.
        var kept = GpuRedaction.ForViewer([
            Proc(1, null, 1.0, 10),
            Proc(2, null, 2.0, 20),
            Proc(3, "kgsm-speech.service", 1.5, 5),
        ]);

        Assert.Equal(2, kept!.Count);
        GpuProcessSample anon = Assert.Single(kept, p => p.Unit is null);
        Assert.Null(anon.Pid);
        Assert.Equal(3.0, anon.MemUsed);
        Assert.Equal(30, anon.SmPct);
    }

    [Fact]
    public void Each_device_keeps_its_own_aggregate()
    {
        // Memory does not pool across cards, so folding two devices' withheld memory into one row would
        // describe a device that does not exist.
        var kept = GpuRedaction.ForViewer([
            Proc(1, null, 1.0, device: 0),
            Proc(2, null, 4.0, device: 1),
        ]);

        Assert.Equal(2, kept!.Count);
        Assert.Equal(1.0, Assert.Single(kept, p => p.DeviceIndex == 0).MemUsed);
        Assert.Equal(4.0, Assert.Single(kept, p => p.DeviceIndex == 1).MemUsed);
    }

    [Fact]
    public void An_idle_unattributed_context_reports_no_utilisation_rather_than_zero()
    {
        // Null is "did no work in the sampling window", which a 0 would make indistinguishable from
        // measured-and-idle. Aggregating must not manufacture the number the source declined to give.
        var kept = GpuRedaction.ForViewer([Proc(1, null, 2.0, sm: null)]);

        Assert.Null(Assert.Single(kept!).SmPct);
    }

    [Fact]
    public void The_totals_a_viewer_sees_match_the_totals_an_operator_sees()
    {
        // The one property worth stating outright: gating changes who is named, never how full the card is.
        IReadOnlyList<GpuProcessSample> full = [
            Proc(1, "kgsm-speech.service", 1.15, 4),
            Proc(2, null, 3.5, 11),
            Proc(3, null, 0.35),
        ];

        var viewer = GpuRedaction.ForViewer(full)!;

        Assert.Equal(full.Sum(p => p.MemUsed), viewer.Sum(p => p.MemUsed), precision: 6);
    }

    [Fact]
    public void No_processes_at_all_stays_absent()
    {
        Assert.Null(GpuRedaction.ForViewer(null));
        Assert.Empty(GpuRedaction.ForViewer([])!);
    }
}
