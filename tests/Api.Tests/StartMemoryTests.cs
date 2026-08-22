using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What a start is expected to cost the node, as published on the server DTO — the input a surface
/// warns from before a start, rather than only reporting the engine's refusal afterwards.
/// <para>
/// The precedence pinned here is the ENGINE's, mirrored: the instance's own cap first, its blueprint's
/// advisory figure second, and nothing at all when neither is declared. A drift between this and kgsm's
/// own order would have the panel warn about one number while the engine judged by another.
/// </para>
/// </summary>
public sealed class StartMemoryTests
{
    [Fact]
    public void The_instance_cap_wins_over_the_blueprint_figure()
    {
        Server s = Build(capMb: 2048, blueprintMinRamMb: 8192);

        // The cap is the cgroup ceiling the watchdog writes to memory.max, so the instance cannot exceed
        // it — it bounds what the node can actually lose, and an operator chose it deliberately.
        Assert.Equal(2048, s.StartMemoryMb);
        Assert.Equal(StartMemorySource.Cap, s.StartMemorySource);
    }

    [Fact]
    public void The_blueprint_figure_is_used_when_the_instance_is_uncapped()
    {
        // 0 is kgsm's spelling of "uncapped" for memory_cap_mb, not a request for no memory.
        Server s = Build(capMb: 0, blueprintMinRamMb: 8192);

        Assert.Equal(8192, s.StartMemoryMb);
        Assert.Equal(StartMemorySource.Blueprint, s.StartMemorySource);
    }

    [Fact]
    public void Source_is_named_so_a_surface_can_say_how_much_to_trust_it()
    {
        // The two differ in standing: a cap is enforced, a blueprint figure is a vendor estimate that is
        // uncurated for many games. A surface that presented the second as measured would be overstating
        // what is known, which is the whole reason the source travels with the number.
        Assert.Equal(StartMemorySource.Cap, Build(capMb: 512, blueprintMinRamMb: null).StartMemorySource);
        Assert.Equal(StartMemorySource.Blueprint, Build(capMb: 0, blueprintMinRamMb: 512).StartMemorySource);
    }

    [Fact]
    public void Nothing_declared_publishes_nothing()
    {
        Server s = Build(capMb: 0, blueprintMinRamMb: null);

        // Both null, never a substituted default. The gate cannot answer either, so a surface must warn
        // about nothing rather than putting an invented requirement in front of a real start. This is the
        // COMMON case today — most blueprints are uncurated.
        Assert.Null(s.StartMemoryMb);
        Assert.Null(s.StartMemorySource);
    }

    [Fact]
    public void A_zero_blueprint_figure_is_not_a_requirement()
    {
        // A blueprint declaring 0 is malformed, not a server that needs no memory. Treating it as a
        // requirement would publish a figure that always fits and quietly suppress the warning.
        Server s = Build(capMb: 0, blueprintMinRamMb: 0);

        Assert.Null(s.StartMemoryMb);
        Assert.Null(s.StartMemorySource);
    }

    private static Server Build(int capMb, int? blueprintMinRamMb) =>
        ServerAggregator.BuildServer(
            id: "srv",
            instance: new Instance
            {
                Name = "srv",
                BlueprintFile = "/blueprints/testgame.bp.yaml",
                MemoryCapMb = capMb,
            },
            statuses: new Dictionary<string, Reading<InstanceRuntimeStatus>>(),
            backupReadings: new Dictionary<string, BackupReading>(),
            metricsById: new Dictionary<string, TheKrystalShip.KGSM.Monitor.Contracts.ServerMetrics>(),
            hostId: "host",
            isStarting: _ => false,
            activeJob: _ => null,
            blueprintMinRamMb: _ => blueprintMinRamMb);
}
