using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Closes a door that belongs to the cluster's auth anchor when this node's cluster has one.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of endpoint carry this, and they fail for the same reason from opposite directions.
/// </para>
/// <para>
/// <b>A door somebody signs in through.</b> A session minted here is scoped to this node and is
/// refused by every other member, so a person who came through it would be signed in to one machine
/// and a stranger on the rest — which is the state one sign-in for the cluster exists to end. A
/// second front door also means a second place a credential is presented, on a machine that does not
/// hold the accounts it would be checked against.
/// </para>
/// <para>
/// <b>A write to an account.</b> The accounts are the anchor's and it is the only writer. A change
/// made here lands in this node's replica, is not versioned by the anchor, and is overwritten by the
/// next thing the anchor publishes about that account — so it would appear to work, and then quietly
/// stop having happened.
/// </para>
/// <para>
/// Reads are untouched, and so is anything that <em>ends</em> a session: revoking takes authority
/// away rather than granting it, and this node holds the rows for the sessions it minted.
/// </para>
/// <para>
/// The decision is <see cref="AnchorHeldGate"/>'s and the vocabulary is <see cref="AnchorHeld"/>'s,
/// both shared with every other member. What is here is the MVC half — the shape this API answers in,
/// which is its own error envelope and nobody else's.
/// </para>
/// </remarks>
public sealed class AnchorHoldsAuthAttribute : ActionFilterAttribute
{
    public override async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<AnchorHeldGate>();

        if (await gate.HolderAsync(context.HttpContext.RequestAborted).ConfigureAwait(false) is not { } elsewhere)
        {
            await next();
            return;
        }

        // The holder's NAME, and never its address. A member of a cluster does not tell a caller
        // where that cluster's accounts are: a browser reaches the anchor because somebody gave it
        // the anchor's address, not because a node it happened to find offered one. A name is not an
        // address — it says this door is not the one, without being a way to discover the one.
        context.HttpContext.Response.Headers[AnchorHeld.HolderHeader] = elsewhere.MemberId;

        context.Result = new ObjectResult(new ErrorEnvelope(new ErrorBody(
            AnchorHeld.Code,
            AnchorHeld.Message(elsewhere.MemberId))))
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
    }
}
