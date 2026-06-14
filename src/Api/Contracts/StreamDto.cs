namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The host capacity sample pushed on the <c>hosts/{id}/metrics</c> topic (M2). It is exactly the
/// mutable, measured portion of the <see cref="Host"/> view (<c>architecture.html §4·a</c>) — the
/// same <see cref="MemCapacity"/>/<see cref="DiskCapacity"/> shapes and units (GiB) the REST
/// <c>GET /hosts</c> emits — minus the stable identity/label and the capability block (capability
/// flips ride their own <c>hosts/{id}/capabilities</c> topic). Built from one monitor snapshot via
/// the shared <c>MetricsMapping</c>, so a tick is byte-identical to the REST capacity figures.
/// <para>Only honestly-measured fields are present: <c>net</c>/<c>temp</c> from the §3·b example are
/// omitted (M1·a never sourced them), never fabricated.</para>
/// </summary>
public sealed record HostMetricsDto(double CpuPct, MemCapacity Mem, IReadOnlyList<DiskCapacity> Disks);

/// <summary>
/// The roster-removal tombstone pushed on the <c>servers</c> topic as <c>server.removed</c> (M2):
/// the instance with this id is gone from the roster, so the client drops it. Distinct from a
/// <c>server.patch</c> (which carries a full element to merge); a removal and a pending patch for
/// the same id share a coalesce key, so the latest event wins.
/// </summary>
public sealed record ServerRemoved(string Id);
