using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Dns.Messages;
using TheKrystalShip.KGSM.Dns.Member;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// This node's game servers, as the cluster's DNS anchor is told them: every instance the engine
/// reports, with the game it runs and the ports it declares.
/// </summary>
/// <remarks>
/// <para>
/// Read from <see cref="InstanceCache"/>, which re-reads the engine on every install and uninstall event
/// and on its own timer, so an install made through the CLI — even one made while this API was stopped —
/// reaches the anchor without anything here watching for it.
/// </para>
/// <para>
/// <b>Only a successful read is an answer.</b> Until the cache has read the engine once it holds an empty
/// placeholder, and sending it would release every game name this node holds; the same goes for a
/// refresh that failed, after which the cache keeps the last good roster but says it could not read.
/// Either way nothing is sent, and the next successful read says it all.
/// </para>
/// </remarks>
internal sealed class EngineInstanceSource(InstanceCache cache) : IDnsInstanceSource
{
    public ValueTask<IReadOnlyList<DnsInstance>?> ReadAsync(CancellationToken ct)
    {
        if (!cache.Loaded || !cache.EngineRead)
            return ValueTask.FromResult<IReadOnlyList<DnsInstance>?>(null);

        List<DnsInstance> instances =
        [
            .. cache.Roster.Select(pair => new DnsInstance(
                Instance: pair.Key,
                Blueprint: pair.Value.Blueprint ?? "",
                Ports: [.. (pair.Value.Ports ?? []).Where(p => p is not null).Select(Port)])),
        ];

        return ValueTask.FromResult<IReadOnlyList<DnsInstance>?>(instances);
    }

    private static DnsPort Port(PortMapping mapping) =>
        new(mapping.Start, mapping.End, mapping.Protocol);
}
