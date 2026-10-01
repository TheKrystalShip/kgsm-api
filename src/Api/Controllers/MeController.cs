using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /me</c> — the caller's own identity on this host (architecture.html §3·f surface, the "Profile"
/// resource). It projects the session bearer's claims: the identity snapshot captured at sign-in, the
/// granted <c>scopes</c>, and the <c>status</c> of the account behind them. What the caller may do is
/// <see cref="MeAccessController"/>.
/// </summary>
/// <remarks>
/// <b>Read-only (a documented divergence — see <see cref="MeResponse"/>):</b> everything here is
/// projected from the session bearer, so there is nothing on this resource for a PATCH to write. The
/// editable half of the Profile page — the UI density, and anything else that must follow a person
/// rather than a browser — is a preference, and preferences are their own resource:
/// <see cref="MePreferencesController"/>, per device with an account-level sync switch.
/// <para/>
/// Gated at <c>[Authorize]</c> — any authenticated caller — so somebody awaiting approval, or whose
/// identity proves no account here, can still read "who am I" honestly instead of being shut out of their
/// own identity. The status is read from this node's replica on every request.
/// <para/>
/// <c>recentLogins</c> is this controller's one database read (see <see cref="MeResponse"/>).
/// <see cref="AppDbContext"/> is request-scoped, resolved once per call, no caching.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController(AppDbContext db, MemberAccess access, ApiOptions options) : ControllerBase
{
    /// <summary>How many recent <c>auth.login</c> rows to surface — a small, fixed window (a login
    /// history, not a full audit page).</summary>
    private const int RecentLoginsLimit = 10;

    [HttpGet]
    public async Task<ActionResult<MeResponse>> Get(CancellationToken ct)
    {
        if (User.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } id)
            return StatusCode(StatusCodes.Status401Unauthorized,
                new ErrorEnvelope(new ErrorBody("unauthorized", "no session")));

        // The audit ActorName for a login is the bare username, not the provider-qualified handle used
        // elsewhere on this DTO (SessionUser.Id) — which is how GET /audit?actor=<name> filters too. A
        // fresh identity with no prior sign-in simply has no auth.login rows -> [].
        IReadOnlyList<AuditRecord> rows = await AuditQueries.RecentLoginsAsync(
            db, HttpContext.RequestServices.GetService<IEventJournalHistory>(), options.HostId,
            id.Username, RecentLoginsLimit, ct);

        IReadOnlyList<RecentLogin> recentLogins = [.. rows
            .Select(r => new RecentLogin(r.Ts, r.Meta?.GetValueOrDefault("userAgent")))];

        return new MeResponse(
            new SessionUser(id.Handle, id.Username, id.Display, id.AvatarUrl),
            id.Scopes,
            recentLogins,
            await StatusAsync(ct));
    }

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        // An auth-disabled host's caller is its synthetic Owner, which no replica holds.
        if (options.AuthDisabled)
            return UserStatuses.Active;

        if (User.FindFirst(AccessClaims.Account)?.Value is not { Length: > 0 } accountId
            || await access.EvaluatorAsync(ct) is not { } evaluator
            || !evaluator.Snapshot.Accounts.TryGetValue(accountId, out AccessAccount? account))
        {
            return StreamAccess.UnknownStatus;
        }

        return StreamAccess.StatusWire(account.Status);
    }
}
