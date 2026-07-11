namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The wire contracts for cluster peer management (<c>PLAN-peers.md §7</c>, P0). camelCase like the rest
/// of the surface; a peer row never carries the cluster secret or a service token — only the roster
/// metadata an admin needs to see/manage membership.
/// </summary>
// GET /peers — one row per roster entry (enabled and disabled).
public sealed record PeerView(
    string Id, string Url, string? Nickname, string NodeId, string Status,
    int? LatencyMs, DateTimeOffset? LastSeen, string ApiVersion, bool Enabled);

/// <summary>GET /peers — the envelope (a bare array would preclude adding pagination/metadata later).</summary>
public sealed record PeerListResponse(IReadOnlyList<PeerView> Peers);

/// <summary>POST /peers — the admin "paste a URL" action. <see cref="Nickname"/> is optional.</summary>
public sealed record PeerAddRequest(string Url, string? Nickname);

/// <summary>201 response for a successful <c>POST /peers</c> — the newly-added row.</summary>
public sealed record PeerAddedResponse(
    string Id, string Url, string? Nickname, string NodeId, string ApiVersion, string Status, bool Enabled);

/// <summary>409 <c>version_mismatch</c> detail — the two sides' route versions, so the admin sees why.</summary>
public sealed record PeerVersionMismatchDetails(string Remote, string Local);

/// <summary>PATCH /peers/{id} — the disable-list toggle. Only <see cref="Enabled"/> is settable today.</summary>
public sealed record PeerPatchRequest(bool Enabled);

/// <summary>GET /peers/identity — this node's identity card, pulled by a candidate's join-via-seed
/// handshake (<c>PLAN-peers.md §7</c>). Cluster-token authed, not user-authed.</summary>
public sealed record PeerIdentityView(
    string NodeId, string ApiVersion, string Build, IReadOnlyList<string> Capabilities);

/// <summary>GET /peers/{id}/latency — the roster row's last-observed liveness sample (§4).
/// <see cref="LatencyMs"/>/<see cref="LastSeen"/> are honest <see langword="null"/> (not omitted — see
/// <c>ApiJson</c>) until this peer's first successful probe.</summary>
public sealed record PeerLatencyView(int? LatencyMs, string Status, DateTimeOffset? LastSeen);
