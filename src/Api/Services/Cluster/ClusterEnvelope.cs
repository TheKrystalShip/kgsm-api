using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The cluster message bus's wire envelope (<c>docs/cluster-message-bus-plan.md §3</c>) — every
/// <c>POST /api/v1/peers/inbox</c> body is exactly this shape, camelCase, with <see cref="Payload"/>
/// left as a raw <see cref="JsonElement"/> so each message type's handler deserializes its own
/// payload shape (the discriminated-union tag is <see cref="Type"/>; there is no shared payload
/// base type).
/// </summary>
/// <param name="Id">
/// A UUID minted once by the sender and stable across every retry — the dedupe key
/// (<see cref="Services.Cluster.ClusterInbox"/>) and the replay defense (a captured-and-replayed
/// envelope is just a duplicate id, ack'd without re-applying).
/// </param>
/// <param name="Type">The discriminated-union tag (e.g. <c>session.revoke</c>) the inbox dispatches
/// on — see the message-type registry, §3.</param>
/// <param name="From">
/// The sender's node id. MUST equal the authenticated cluster service token's <c>iss</c>
/// (<see cref="Services.Cluster.ClusterPrincipal.NodeId"/>) — <see cref="Controllers.PeersController"/>
/// rejects a mismatch with <c>403 from_mismatch</c> before this envelope ever reaches the inbox.
/// </param>
/// <param name="Ts">Informational only (diagnostics/ordering hints) — correctness never depends on
/// it (§3).</param>
/// <param name="Payload">The type-specific, camelCase payload. Left as a raw <see cref="JsonElement"/>
/// (never a shared base DTO) so a handler owns its own payload's shape end to end.</param>
public sealed record ClusterEnvelope(
    string Id,
    string Type,
    string From,
    DateTimeOffset Ts,
    [property: JsonPropertyName("payload")] JsonElement Payload);
