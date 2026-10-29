using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Transfers;
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
/// <para>
/// <b>A server that arrived by transfer inherits the game name it had.</b> An instance whose target-side
/// transfer record is committed carries <see cref="DnsInheritance"/> naming the source member, the source
/// instance and the name it held, for as long as the record exists; the anchor hands the name over to
/// it, and an instance that already holds a name ignores it. The anchor names an instance the first time
/// it hears of it, so the instance must not be heard of before its record is committed: while an import
/// is finishing, the engine can register the instance before this node has recorded which one it is, and
/// nothing is sent until the record says.
/// </para>
/// </remarks>
internal sealed class EngineInstanceSource(InstanceCache cache, TransferStore transfers) : IDnsInstanceSource
{
    public ValueTask<IReadOnlyList<DnsInstance>?> ReadAsync(CancellationToken ct)
    {
        if (!cache.Loaded || !cache.EngineRead || !transfers.Loaded)
            return ValueTask.FromResult<IReadOnlyList<DnsInstance>?>(null);

        Dictionary<string, DnsInheritance> inherits = new(StringComparer.Ordinal);
        foreach (TransferEntity r in transfers.Snapshot)
        {
            if (r.Role != TransferRole.Target) continue;

            if (r.State == TransferState.Running && r.Phase == TransferPhase.Importing)
                return ValueTask.FromResult<IReadOnlyList<DnsInstance>?>(null);

            if (r.State == TransferState.Committed && r.TargetInstanceId is { Length: > 0 } instance)
                inherits[instance] = new DnsInheritance(r.PeerMemberId, r.InstanceId, r.GameName);
        }

        List<DnsInstance> instances =
        [
            .. cache.Roster.Select(pair => new DnsInstance(
                Instance: pair.Key,
                Blueprint: pair.Value.Blueprint ?? "",
                Ports: [.. (pair.Value.Ports ?? []).Where(p => p is not null).Select(Port)],
                Inherits: inherits.GetValueOrDefault(pair.Key))),
        ];

        return ValueTask.FromResult<IReadOnlyList<DnsInstance>?>(instances);
    }

    private static DnsPort Port(PortMapping mapping) =>
        new(mapping.Start, mapping.End, mapping.Protocol);
}
