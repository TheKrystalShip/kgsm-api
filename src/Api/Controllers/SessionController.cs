using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.Api.Services.Cluster;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// The session-revocation surface (authority: <c>Services/Auth/CLAUDE.md</c>). The
/// registry (<see cref="SessionStore"/>) is the authority on "is this session still alive"; this
/// controller is its read + control surface: list the active set, revoke one/all of your own
/// sessions, or — admin only — revoke any user's session(s) cross-user (a deliberate power
/// grant).
/// <para>
/// Root-routed (<c>/auth/...</c>, NOT under <c>/api/v1</c>) — same posture as
/// <see cref="AuthController"/>: these are session-lifecycle endpoints, not domain resources.
/// </para>
/// <para>
/// <b>Tier gating.</b> Class-level <see cref="AuthPolicy.Viewer"/> (every endpoint needs at least a
/// valid session); the two admin endpoints ALSO carry a method-level
/// <see cref="AuthPolicy.Admin"/> attribute. ASP.NET Core AND-combines multiple <c>[Authorize]</c>
/// attributes on the same action (class + method), so this "tightens" rather than replaces the
/// class policy — same pattern as <c>HostsController.Patch</c>. A viewer hitting an admin endpoint
/// still gets a clean <c>403</c> (never a <c>401</c> — they DO have a valid bearer, just the wrong
/// tier), preserving the secure-by-default 401-vs-403 split (<c>Services/Auth/CLAUDE.md</c>).
/// </para>
/// </summary>
[ApiController]
[Authorize(Policy = AuthPolicy.Viewer)]
public sealed class SessionController(
    SessionStore sessions,
    ISessionValidator sessionValidator,
    ApiOptions options,
    ApiJournal journal,
    IClusterBus clusterBus,
    RosterClusterTargetProvider rosterTargets) : ControllerBase
{
    /// <summary>
    /// Fans a "log out user everywhere" out to the rest of the cluster (<c>PLAN-peers.md §P1</c>) —
    /// called AFTER this node's own local revoke + evict has already committed, from both
    /// revoke-all-for-user surfaces (self <c>all:true</c> and the admin by-user endpoint). No-ops when
    /// this node isn't in a cluster; targets only first-hand-alive peers
    /// (<see cref="RosterClusterTargetProvider.GetEnabledTargetsAsync"/>) — never this node itself, since
    /// the local effect already happened in-process. <paramref name="rawDiscordId"/> is the bare Discord
    /// id (no <c>discord:</c> prefix) — <c>SessionRevokeHandler</c> re-adds it on the receiving end.
    /// </summary>
    private async Task FanOutRevokeAllAsync(string rawDiscordId, CancellationToken ct)
    {
        if (!options.ClusterEnabled)
            return;

        IReadOnlyList<ClusterTarget> targets = await rosterTargets.GetEnabledTargetsAsync(ct).ConfigureAwait(false);
        await clusterBus.EnqueueAsync(
            "session.revoke",
            new { scope = "user", discordId = rawDiscordId },
            targets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /auth/sessions</c> — the caller's own active session set (D5 fields), or, for an
    /// admin passing <c>?userId=</c>, another user's. <c>userId</c> is the prefixed handle
    /// (<c>discord:&lt;id&gt;</c>) — the same shape <see cref="SessionRecord.UserId"/> returns, so an
    /// admin can round-trip a value straight from one response into the next request's query string.
    /// <b>Decision (plan-consistent, not spelled out verbatim in §5):</b> a non-admin caller's
    /// <c>?userId=</c> is silently IGNORED (always scoped to self) rather than 403 — the plan states
    /// "viewer is self-only" without picking between "ignore" and "reject"; ignoring keeps a viewer
    /// who copy-pastes an admin's URL from erroring on a param they can't use, at no security cost
    /// (the scope is still enforced server-side regardless of what they pass).
    /// </summary>
    [HttpGet("/auth/sessions")]
    public async Task<ActionResult<SessionsPage>> List([FromQuery] string? userId, CancellationToken ct)
    {
        if (User.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } caller)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");

        string callerUserId = caller.Handle;
        KgsmTier tier = SessionClaims.ReadTier(ci);
        string? callerSid = SessionClaims.ReadSessionId(ci);

        // Admin override: only an admin's ?userId= is honored (D4 — admin is a session operator).
        string targetUserId = tier == KgsmTier.Admin && !string.IsNullOrWhiteSpace(userId)
            ? userId!
            : callerUserId;

        IReadOnlyList<SessionEntry> rows = await sessions.ListActiveAsync(targetUserId, options.HostId, ct);
        var data = rows
            .Select(r => new SessionRecord(r.Id, r.UserId, r.Created, r.LastSeen, r.Expires, r.UserAgent,
                Current: r.Id == callerSid))
            .ToList();
        return new SessionsPage(data);
    }

    /// <summary>
    /// <c>POST /auth/session/revoke</c> — viewer self-revoke. Body <c>{ sid?, all? }</c>:
    /// <c>all:true</c> revokes every session the caller owns (including the calling one — "log out
    /// everywhere"); <c>sid</c> revokes that one session, but ONLY if it belongs to the caller
    /// (ownership checked via <see cref="SessionStore.GetByIdAsync"/> — else <c>404</c>, never a
    /// <c>403</c>, so this endpoint can't be used to probe whether a sid exists for another user);
    /// neither set revokes the calling session (logout-equivalent, but through the revocation
    /// surface rather than <c>/auth/logout</c>). <b>Decision:</b> both set at once is rejected
    /// <c>400</c> (the DTO doc says "exactly one, or neither" — silently preferring one over the
    /// other would surprise a caller who set both by a client bug). Always <c>204</c> on success,
    /// idempotent (revoking an already-dead session is a no-op, not an error).
    /// </summary>
    [HttpPost("/auth/session/revoke")]
    public async Task<IActionResult> RevokeSelf([FromBody] RevokeRequest? body, CancellationToken ct)
    {
        if (User.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } caller)
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session");

        string callerUserId = caller.Handle;
        string? callerSid = SessionClaims.ReadSessionId(ci);
        bool hasSid = !string.IsNullOrWhiteSpace(body?.Sid);
        bool all = body?.All == true;

        if (hasSid && all)
            return Error(StatusCodes.Status400BadRequest, "bad_request",
                "sid and all are mutually exclusive — set exactly one, or neither.");

        if (all)
        {
            IReadOnlyList<string> revokedSids = await sessions.RevokeAllForUserAsync(callerUserId, options.HostId, ct);
            foreach (string sid in revokedSids)
                sessionValidator.Evict(sid);
            await RecordAsync("all", ci, sid: null, count: revokedSids.Count,
                targetUserId: callerUserId, ct: ct);
            await FanOutRevokeAllAsync(caller.Subject, ct);
            return NoContent();
        }

        string targetSid = hasSid ? body!.Sid! : callerSid ?? "";
        if (targetSid.Length == 0)
            // No sid supplied and the calling bearer itself carries none (a pre-M4·c token would
            // already have been rejected by the pipeline, so this is effectively unreachable — kept
            // as an honest guard rather than a silent no-op).
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "no session id on the calling bearer");

        if (hasSid)
        {
            // Ownership check — ONLY the caller's own session can be self-revoked by sid. A viewer
            // must not be able to revoke another user's session by guessing/leaking a sid; that is
            // admin-only power (POST /auth/sessions/{sid}/revoke). A mismatch or missing row both
            // resolve to 404 (never leaks "exists but isn't yours" via a 403).
            SessionEntry? owned = await sessions.GetByIdAsync(targetSid, ct);
            if (owned is null || owned.UserId != callerUserId)
                return NotFound();
        }

        bool revoked = await sessions.RevokeAsync(targetSid, ct);
        if (revoked)
            sessionValidator.Evict(targetSid);
        // Idempotent either way — a revoke on an already-dead sid still reports success (204), but
        // only stamp an audit row when this call actually did something (avoid a misleading "revoke"
        // trail entry for a no-op repeat call).
        if (revoked)
            await RecordAsync("self", ci, sid: targetSid, count: 1,
                targetUserId: callerUserId, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// <c>POST /auth/sessions/{sid}/revoke</c> — admin cross-user revoke by sid (D4). <c>404</c> when
    /// no row exists for <paramref name="sid"/>; otherwise always <c>204</c> (idempotent — revoking an
    /// already-revoked row is a no-op, not an error). Evicts the validator cache so the target's next
    /// access 401s immediately rather than waiting for the cache TTL backstop.
    /// </summary>
    [HttpPost("/auth/sessions/{sid}/revoke")]
    [Authorize(Policy = AuthPolicy.Admin)] // cross-user power — admin only, tightening the class-level viewer gate
    public async Task<IActionResult> RevokeAdmin(string sid, CancellationToken ct)
    {
        SessionEntry? target = await sessions.GetByIdAsync(sid, ct);
        if (target is null)
            return NotFound();

        await sessions.RevokeAsync(sid, ct);
        sessionValidator.Evict(sid);

        if (User.Identity is ClaimsIdentity ci)
            await RecordAsync("admin", ci, sid: sid, count: 1,
                targetUserId: target.UserId, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// <c>POST /auth/users/{userId}/sessions/revoke-all</c> — "log out user everywhere" (admin, D4).
    /// <paramref name="userId"/> is the prefixed handle (<c>discord:&lt;id&gt;</c>), the same shape
    /// <see cref="SessionRecord.UserId"/> returns. Always <c>204</c> — a target with zero active
    /// sessions is not an error (there's nothing to reject: "log this user out" trivially succeeds
    /// when they're already out), unlike the by-sid endpoint above which 404s on a genuinely
    /// nonexistent row.
    /// </summary>
    [HttpPost("/auth/users/{userId}/sessions/revoke-all")]
    [Authorize(Policy = AuthPolicy.Admin)] // cross-user power — admin only, tightening the class-level viewer gate
    public async Task<IActionResult> RevokeAllForUser(string userId, CancellationToken ct)
    {
        IReadOnlyList<string> revokedSids = await sessions.RevokeAllForUserAsync(userId, options.HostId, ct);
        foreach (string sid in revokedSids)
            sessionValidator.Evict(sid);

        if (User.Identity is ClaimsIdentity ci)
            await RecordAsync("admin", ci, sid: null, count: revokedSids.Count,
                targetUserId: userId, ct: ct);

        // userId is the provider-qualified handle; the cluster payload carries the bare subject, which
        // the receiving handler re-qualifies (see FanOutRevokeAllAsync + SessionRevokeHandler). An
        // unqualified value is passed through as-is rather than rejected — it is already the shape the
        // payload wants.
        string subject = KgsmActor.TryParse(userId, out _, out string parsed) ? parsed : userId;
        await FanOutRevokeAllAsync(subject, ct);
        return NoContent();
    }

    // Record a revocation in this API's own journal. No kgsm event backs a revocation — nothing runs
    // on the engine — so this API is genuinely the author, not a second writer for somebody else's
    // action. Best-effort: a failed write is logged and never turns a successful revoke into an error,
    // because the revoke itself has already committed.
    //
    // `scope` is what separates the three: `self` (one of the caller's own), `all` (every session the
    // caller holds), `admin` (somebody else's). It is one fact told three ways, so it is one event with
    // a scope rather than three types — who was affected and who did it are already on the row.
    private Task RecordAsync(
        string scope, ClaimsIdentity ci, string? sid, int? count, string targetUserId,
        CancellationToken ct)
    {
        KgsmIdentity? actor = SessionClaims.ReadIdentity(ci);
        if (actor is null)
            return Task.CompletedTask;

        return journal.SessionRevokedAsync(
            scope,
            // Whose sessions ended — which for an admin revoke is NOT the actor. Collapsing the two
            // would make "who was logged out" unanswerable on exactly the rows where it matters.
            userId: targetUserId,
            username: actor.Username,
            sid: sid,
            count: count,
            actor: KgsmActor.Format(actor.Provider, actor.Username),
            origin: AuditOrigin.Ui,
            ct: ct);
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ErrorEnvelope(new ErrorBody(code, message)));
}
