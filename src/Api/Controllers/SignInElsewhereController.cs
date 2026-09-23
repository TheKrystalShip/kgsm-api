using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// What a caller asking this node how to sign in is told: which member does.
/// </summary>
/// <remarks>
/// <para>
/// This node signs nobody in. Every account belongs to the cluster's auth anchor and every session is
/// minted there, so any <c>/auth</c> path on a node answers <c>503</c> — the door exists in the
/// cluster, and this is not it — with the holder named on <see cref="AnchorHeld.HolderHeader"/> in the
/// shared vocabulary every member refuses in. A client routes on the code and the header.
/// </para>
/// <para>
/// The holder's <b>name</b>, never its address: a browser reaches the anchor because whoever deployed
/// it or the person using it supplied the address. When this node has not heard of a holder — no
/// cluster secret, or gossip that has not arrived — the answer says so rather than naming nobody as
/// if that were a member.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
public sealed class SignInElsewhereController(AnchorHeldGate gate) : ControllerBase
{
    /// <summary>The code for a node whose cluster has no member holding its accounts.</summary>
    public const string NoHolderCode = "auth_holder_unknown";

    [Route("/auth/{**path}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Refuse(CancellationToken ct)
    {
        if (await gate.HolderAsync(ct).ConfigureAwait(false) is { } holder)
        {
            Response.Headers[AnchorHeld.HolderHeader] = holder.MemberId;
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorEnvelope(new ErrorBody(
                AnchorHeld.Code, AnchorHeld.Message(holder.MemberId))));
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorEnvelope(new ErrorBody(
            NoHolderCode,
            "No member of this node's cluster holds its accounts, so nobody can sign in to it.")));
    }
}
