namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// One message-type handler in the cluster bus's discriminated union (<c>docs/cluster-message-bus-plan.md
/// §3/§7</c>). Registered in DI as a collection (<c>IEnumerable&lt;IClusterMessageHandler&gt;</c>,
/// <c>Startup</c>) and dispatched by <see cref="Type"/> — <see cref="Services.Cluster.ClusterInbox"/>
/// resolves the set once and looks a handler up by the incoming envelope's <c>type</c>. Adding a new
/// message type means adding a new implementation here, never touching the dispatcher.
/// </summary>
/// <remarks>
/// <b>Handlers MUST be idempotent</b> — applying the same envelope twice must be harmless. This is
/// the second of the plan's two idempotency layers (the first is the inbox's processed-id ledger):
/// even if a crash lands between "handler succeeded" and "ledger row recorded," a redelivered
/// envelope re-running the handler must never double-apply its effect. <see cref="HandleAsync"/>
/// should throw only for a genuinely <em>transient</em> failure (e.g. the local DB is locked) — the
/// inbox maps a thrown exception to a <c>500</c> so the sender retries; a permanent/malformed-payload
/// condition should be logged and swallowed (a no-op), never thrown, or the sender retries forever
/// against a message this node can never accept.
/// </remarks>
public interface IClusterMessageHandler
{
    /// <summary>The discriminated-union <c>type</c> tag this handler answers to (e.g.
    /// <c>session.revoke</c>) — matched against <see cref="ClusterEnvelope.Type"/>.</summary>
    string Type { get; }

    /// <summary>Apply the envelope's effect locally. See the remarks on <see cref="IClusterMessageHandler"/>
    /// for the idempotency + throw-only-for-transient-failure contract.</summary>
    Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct);
}
