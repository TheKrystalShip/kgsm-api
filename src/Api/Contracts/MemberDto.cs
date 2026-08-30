namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The wire contracts for cluster membership. camelCase like the rest of the surface; a member row never
/// carries the cluster secret or a service token — only the roster metadata a person needs in order to see
/// and manage membership.
/// </summary>
/// <param name="Id">This node's local handle on the row.</param>
/// <param name="Url">The address this node calls the member on.</param>
/// <param name="Nickname">An operator-assigned label, or null.</param>
/// <param name="MemberId">The member's own identity.</param>
/// <param name="Kind">Node or anchor.</param>
/// <param name="Status">This node's own first-hand probe result: reachable, unreachable, or unknown.</param>
/// <param name="Membership">What the mesh converged on — alive, suspect, dead or left, or the derived
/// joining when gossip says alive and this node has never reached it. Two axes, never conflated.</param>
/// <param name="LatencyMs">The last successful probe's round trip, or null when never reached.</param>
/// <param name="LastSeen">When the member was last reached, or last authenticated a call here.</param>
/// <param name="ApiVersion">A node's route version. Empty for an anchor, which serves none.</param>
/// <param name="Enabled">Whether this node accepts the member's calls.</param>
public sealed record MemberView(
    string Id, string Url, string? Nickname, string MemberId, string Kind, string Status, string Membership,
    int? LatencyMs, DateTimeOffset? LastSeen, string ApiVersion, bool Enabled);

/// <summary>The roster envelope. A bare array would preclude adding pagination or metadata later.</summary>
public sealed record MemberListResponse(IReadOnlyList<MemberView> Members);

/// <summary>
/// One enabled member as a viewer sees it. Deliberately leaner than <see cref="MemberView"/>: no local
/// handle, no enabled flag — a management state only an admin toggles — and no route version. Just enough
/// to know a member exists, what it is, how to reach it, and whether it is alive.
/// </summary>
public sealed record ClusterMemberView(
    string MemberId, string Label, string Kind, string ClientUrl, string Membership, string Status, int? LatencyMs);

/// <summary>The viewer-tier roster envelope.</summary>
public sealed record ClusterMembersResponse(IReadOnlyList<ClusterMemberView> Members);

/// <summary>The admin "paste a URL" action. The nickname is optional.</summary>
public sealed record MemberAddRequest(string Url, string? Nickname);

/// <summary>The newly-added row, answered on a successful join.</summary>
public sealed record MemberAddedResponse(
    string Id, string Url, string? Nickname, string MemberId, string Kind, string ApiVersion, string Status,
    bool Enabled);

/// <summary>The two route versions that disagreed, so a refusal names both rather than only itself.</summary>
public sealed record MemberVersionMismatchDetails(string Remote, string Local);

/// <summary>
/// Which member holds one of the cluster's capabilities.
/// </summary>
/// <remarks>
/// A capability belongs to the cluster rather than to any member, so this is not a fact read off a
/// roster row. <paramref name="Held"/> tells "the cluster decided nobody" apart from "nobody holds
/// it", which are different answers an operator acts on differently.
/// </remarks>
/// <param name="Capability">The capability's stable name — <c>auth</c> today.</param>
/// <param name="MemberId">The member holding it, empty when nobody does.</param>
/// <param name="Held">Whether anybody holds it.</param>
/// <param name="Version">The assignment's version. A reassignment supersedes a lower one.</param>
/// <param name="SetBy">The member whose copy this came from, which is what breaks a version tie.</param>
/// <param name="Orphaned">
/// Whether the member named holds no place in the roster any more. A holder whose machine died is
/// reaped by the failure timers while the assignment survives it, and the result is the one cluster
/// state where everything reads healthy and nothing works: every member stands by against a holder
/// that will never answer, the capability is not served, and nothing errors because nothing failed.
/// Reported rather than repaired — reassigning is a decision, and promoting automatically is the
/// election this design rejects.
/// </param>
public sealed record ClusterCapabilityView(
    string Capability, string MemberId, bool Held, long Version, string SetBy, bool Orphaned);

/// <summary>Every capability assignment this node holds a copy of.</summary>
public sealed record ClusterCapabilitiesResponse(IReadOnlyList<ClusterCapabilityView> Capabilities);

/// <summary>
/// The admin's reassignment. An empty member id records <em>deliberately nobody</em>, which converges
/// as a decision rather than reading as an assignment nobody has heard yet.
/// </summary>
public sealed record ClusterCapabilityAssignRequest(string MemberId);

/// <summary>The disable toggle. Only the flag is settable.</summary>
public sealed record MemberPatchRequest(bool Enabled);

/// <summary>A member's last-observed liveness sample. The latency and last-seen are honest
/// <see langword="null"/> — not omitted — until the first successful probe.</summary>
public sealed record MemberLatencyView(int? LatencyMs, string Status, DateTimeOffset? LastSeen);

/// <summary>
/// This node's own capacity, projected for another member's fan-out. A lean projection of the host capacity
/// strip: a capacity read needs how loaded a node is, not its full diagnostics. Capacity is honest
/// <see langword="null"/> when no metrics snapshot exists, never fabricated.
/// </summary>
public sealed record ClusterResourcesView(
    string Id, string Label, string Status, double? CpuPct, MemCapacity? Mem, IReadOnlyList<DiskCapacity>? Disks);
