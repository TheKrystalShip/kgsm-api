using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using TheKrystalShip.Api.Contracts;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Where this cluster's accounts are held, and therefore whether this node still answers for them.
/// </summary>
/// <remarks>
/// <para>
/// A standalone host holds its own accounts and is the only place anybody can sign in, which is what
/// most installs are and what this API has always done. A host whose cluster has an <b>auth
/// anchor</b> holds a read-only replica instead: the accounts are the anchor's, one member writes
/// them, and a person signs in once against that member rather than once per machine.
/// </para>
/// <para>
/// So the answer is not a setting. It is read from cluster state — a capability somebody holds — and
/// it changes on its own when an anchor joins or is reassigned, without this node being reconfigured
/// or restarted.
/// </para>
/// </remarks>
public sealed class AnchorHeldGate(
    ClusterStateStore clusterState,
    MembersStore members,
    ClusterOptions cluster)
{
    /// <summary>Where a person should go instead, when it is not here.</summary>
    /// <param name="MemberId">The member holding the cluster's accounts.</param>
    /// <param name="Url">The address a browser signs in at, when the holder states one.</param>
    public sealed record Elsewhere(string MemberId, string? Url);

    /// <summary>
    /// The member holding the cluster's accounts, or <see langword="null"/> when this node holds its
    /// own.
    /// </summary>
    /// <remarks>
    /// Null means "nobody else answers for these accounts", which covers a standalone host and a
    /// clustered one whose cluster has no anchor. Both are the same fact from here: there is nowhere
    /// else to send anybody, so this node is the only door there is and closing it would lock
    /// everybody out of their own machine.
    /// </remarks>
    public async Task<Elsewhere?> HolderAsync(CancellationToken ct)
    {
        if (!cluster.Enabled)
            return null;

        string? holder = await clusterState.HolderAsync(ClusterCapability.Auth, ct).ConfigureAwait(false);
        if (holder is null || string.Equals(holder, cluster.MemberId, StringComparison.Ordinal))
            return null;

        MemberRow? row = await members.GetByMemberIdAsync(holder, ct).ConfigureAwait(false);

        // A holder this node has an assignment for but no roster row for is still the holder. The
        // accounts are not this node's to answer for merely because it cannot currently see who does.
        string? url = row?.Read(ClusterAuthFacts.SignInUrl);
        if (string.IsNullOrWhiteSpace(url))
            url = string.IsNullOrWhiteSpace(row?.Url) ? null : row.Url;

        return new Elsewhere(holder, url);
    }
}

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

        // Named on a header as well as in the message, so a client can route to the member that can
        // actually answer rather than reading English to find out. The same header the anchor sets
        // when it is standing by, for the same reason.
        context.HttpContext.Response.Headers["X-Kgsm-Auth-Holder"] = elsewhere.MemberId;
        if (elsewhere.Url is { Length: > 0 } url)
            context.HttpContext.Response.Headers["X-Kgsm-Auth-Url"] = url;

        // 503 rather than 404 or 403: the door exists and this is not a refusal of the caller. It is
        // this node saying it is not the one that answers, which is a different fact and the one that
        // tells somebody where to go.
        context.Result = new ObjectResult(new ErrorEnvelope(new ErrorBody(
            "auth_held_by_anchor",
            elsewhere.Url is { Length: > 0 } at
                ? $"This cluster's accounts are held by '{elsewhere.MemberId}'. Sign in at {at}."
                : $"This cluster's accounts are held by '{elsewhere.MemberId}'.")))
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
    }
}
