namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// One outbound delivery target for <see cref="IClusterBus.EnqueueAsync"/> — a peer node's id plus the
/// base URL the outbox drainer POSTs <c>/api/v1/peers/inbox</c> onto (<c>docs/cluster-message-bus-plan.md
/// §6</c>). There is no <c>Peers</c> table yet (a later, separate peer-foundation milestone,
/// <c>PLAN-peers.md</c> P0) — until it lands, a caller of <see cref="IClusterBus.EnqueueAsync"/> supplies
/// the target set itself (e.g. "every configured peer" from whatever config/registry it already reads).
/// </summary>
/// <param name="NodeId">The recipient's cluster node id — becomes <see cref="Data.OutboxMessage.TargetNodeId"/>.</param>
/// <param name="Url">The recipient's base URL (e.g. <c>https://node-b:8097</c>, no trailing slash required) —
/// becomes <see cref="Data.OutboxMessage.TargetUrl"/>; the drainer POSTs to <c>{Url}/api/v1/peers/inbox</c>.</param>
public sealed record ClusterTarget(string NodeId, string Url);
