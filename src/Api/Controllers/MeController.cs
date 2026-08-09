using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /me</c> — the caller's own identity and what it may do on this host (architecture.html §3·f
/// surface, the "Profile" resource). It projects the session bearer's claims: the Discord identity
/// snapshot captured at login plus the resolved authorization <c>tier</c> and granted <c>scopes</c>.
/// The SPA gates which controls it renders on <c>tier</c>, so this is the surface it reads on load.
/// </summary>
/// <remarks>
/// <b>Read-only (a documented divergence — see <see cref="MeResponse"/>):</b> the editable Profile half
/// (display name, density) needs a per-panel preference store that is deliberately not built, so PATCH is
/// deferred. The honest delta this adds over <c>GET /auth/session</c> (which returns <c>{ user, scopes }</c>)
/// is the <c>tier</c> — the one fact the SPA needs to decide what to show, and the reason <c>/me</c> exists
/// as its own resource rather than the SPA inferring authority from a 403.
/// <para/>
/// Gated at <c>[Authorize]</c> — any authenticated caller, mirroring <c>/auth/session</c>, NOT viewer — so a
/// <c>none</c>-tier caller (identity verified, no role on this host) can still read "who am I / why am I 403
/// elsewhere" honestly instead of being shut out of their own identity. The tier is read from the bearer's
/// claim verbatim; no role re-check happens here (that lives in the login/refresh path).
/// <para/>
/// <b>M4·c Increment 7</b> added <c>recentLogins</c> — this controller's FIRST DB read (see
/// <see cref="MeResponse"/> for the honesty rationale). <see cref="AppDbContext"/> is injected the same
/// way <c>AuditController</c> does it: request-scoped, resolved once per call, no caching.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController(AppDbContext db) : ControllerBase
{
    /// <summary>How many recent <c>auth.login</c> rows to surface — a small, fixed window (a login
    /// history, not a full audit page); matches the plan's Increment 7 spec.</summary>
    private const int RecentLoginsLimit = 10;

    [HttpGet]
    public async Task<ActionResult<MeResponse>> Get(CancellationToken ct)
    {
        if (User.Identity is not ClaimsIdentity ci || SessionClaims.ReadIdentity(ci) is not { } id)
            return StatusCode(StatusCodes.Status401Unauthorized,
                new ErrorEnvelope(new ErrorBody("unauthorized", "no session")));

        // ⚠ The audit ActorName for a login is the BARE Discord username (e.g. "haru"), NOT the
        // "discord:"-prefixed handle used elsewhere on this DTO (SessionUser.Id) — mirrors how
        // AuthController.RecordAuthAsync stamps Actor = new AuditActor(ActorKind.User, id.Username,
        // ActorProvider.Discord), and how GET /audit?actor=haru already filters. A fresh identity with
        // no prior login (e.g. a synthetic test/dev token) simply has no auth.login rows -> [].
        List<AuditEntry> rows = await AuditQueries.RecentByActionAsync(
            db, AuditAction.AuthLogin, id.Username, RecentLoginsLimit, ct);

        // Reuse AuditMapping.ToRecord for the Meta JSON-blob parse (same try/catch-guarded
        // deserialization the audit read path already uses) rather than duplicating it here; the only
        // projection this controller adds is picking `userAgent` out of the parsed Meta as `Device`.
        IReadOnlyList<RecentLogin> recentLogins = rows
            .Select(r => AuditMapping.ToRecord(r))
            .Select(r => new RecentLogin(r.Ts, r.Meta?.GetValueOrDefault("userAgent")))
            .ToList();

        return new MeResponse(
            new SessionUser(id.Handle, id.Username, id.Display, id.AvatarUrl),
            KgsmTiers.ToWire(SessionClaims.ReadTier(ci)),
            id.Scopes,
            recentLogins);
    }
}
