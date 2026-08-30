using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// Where this cluster signs people in — the one question a browser with nothing stored has to be able
/// to ask.
/// </summary>
/// <remarks>
/// <para>
/// A person opens the Control Panel pointed at whichever member they have an address for, and that
/// member tells them where the cluster's front door is. The alternatives were a build per cluster, or
/// asking the person to paste a second address — and any member already knows the answer, because the
/// assignment and the anchor's own statement about itself are both gossiped.
/// </para>
/// <para>
/// <b>Unauthenticated, and that is the point rather than an exemption.</b> The caller has no session;
/// that is why it is asking. It learns that this cluster has an auth anchor and where to knock, which
/// is exactly what the sign-in page it is about to be sent to would tell it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/cluster")]
public sealed class ClusterAuthController(
    ClusterStateStore clusterState,
    MembersStore members) : ControllerBase
{
    /// <summary>
    /// <c>GET /api/v1/cluster/auth</c> — which member holds the cluster's accounts, and where a
    /// browser reaches it.
    /// </summary>
    [HttpGet("auth")]
    [AllowAnonymous]
    public async Task<ClusterAuthAnchor> Anchor(CancellationToken ct)
    {
        ClusterAssignment? assignment =
            await clusterState.GetAsync(ClusterCapability.Auth, ct).ConfigureAwait(false);

        if (assignment is not { IsHeld: true })
            return new ClusterAuthAnchor(Held: false, MemberId: "", Url: null, Orphaned: false);

        MemberRow? holder =
            await members.GetByMemberIdAsync(assignment.MemberId, ct).ConfigureAwait(false);

        if (holder is null)
        {
            // Held on paper, served by nobody. Distinguished from an unreachable anchor because they
            // read identically at a browser and are fixed differently: this one is a reassignment,
            // the other is waiting.
            return new ClusterAuthAnchor(
                Held: true, MemberId: assignment.MemberId, Url: null, Orphaned: true);
        }

        // What the anchor says about itself first. The roster address is what MEMBERS reach it at,
        // which is the same string here and is not the same question — falling back to it keeps this
        // answerable against an anchor that states nothing, and never prefers it to a statement.
        string? url = holder.Read(ClusterAuthFacts.SignInUrl);
        if (string.IsNullOrWhiteSpace(url))
            url = string.IsNullOrWhiteSpace(holder.Url) ? null : holder.Url;

        return new ClusterAuthAnchor(
            Held: true, MemberId: assignment.MemberId, Url: url, Orphaned: false);
    }
}
