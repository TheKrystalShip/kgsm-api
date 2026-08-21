using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Users;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Controllers;

/// <summary>
/// <c>GET /me</c> — the caller's own identity and what it may do on this host (architecture.html §3·f
/// surface, the "Profile" resource). It projects the session bearer's claims: the identity snapshot
/// captured at login, plus the authorization <c>tier</c>, the granted <c>scopes</c>, and the
/// <c>status</c> of the account behind them. The SPA gates which controls it renders on <c>tier</c>,
/// so this is the surface it reads on load.
/// </summary>
/// <remarks>
/// <b>Read-only (a documented divergence — see <see cref="MeResponse"/>):</b> everything here is
/// projected from the session bearer, so there is nothing on this resource for a PATCH to write. The
/// editable half of the Profile page — the UI density, and anything else that must follow a person
/// rather than a browser — is a preference, and preferences are their own resource:
/// <see cref="MePreferencesController"/>, per device with an account-level sync switch.
/// The honest delta this adds over <c>GET /auth/session</c> (which returns <c>{ user, scopes }</c>)
/// is the <c>tier</c> — the one fact the SPA needs to decide what to show, and the reason <c>/me</c> exists
/// as its own resource rather than the SPA inferring authority from a 403.
/// <para/>
/// Gated at <c>[Authorize]</c> — any authenticated caller, mirroring <c>/auth/session</c>, NOT viewer — so a
/// <c>none</c>-tier caller — one awaiting approval, or one whose identity proves no account here — can still
/// read "who am I / why am I 403 elsewhere" honestly instead of being shut out of their own identity. The
/// tier is the one the account store resolved when this request's token was validated, not the one the token
/// was minted with, so a demotion shows here as soon as it shows on every gate.
/// <para/>
/// <b>M4·c Increment 7</b> added <c>recentLogins</c> — this controller's FIRST DB read (see
/// <see cref="MeResponse"/> for the honesty rationale). <see cref="AppDbContext"/> is injected the same
/// way <c>AuditController</c> does it: request-scoped, resolved once per call, no caching.
/// </remarks>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController(AppDbContext db, UserDirectory users, ApiOptions options) : ControllerBase
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
        // Reads the merged feed, so the list spans the moment this API started recording sign-ins in
        // its own journal rather than stopping dead at it. The only projection this controller adds is
        // picking `userAgent` out of the shaped row's meta as `Device`.
        IReadOnlyList<AuditRecord> rows = await AuditQueries.RecentLoginsAsync(
            db, HttpContext.RequestServices.GetService<IEventJournalHistory>(), options.HostId,
            id.Username, RecentLoginsLimit, ct);

        IReadOnlyList<RecentLogin> recentLogins = [.. rows
            .Select(r => new RecentLogin(r.Ts, r.Meta?.GetValueOrDefault("userAgent")))];

        return new MeResponse(
            new SessionUser(id.Handle, id.Username, id.Display, id.AvatarUrl),
            // The tier on the claims identity, which the authority resolution at token validation has
            // already replaced with what the account store says now — so this is live, not the value
            // the token was minted with.
            KgsmTiers.ToWire(SessionClaims.ReadTier(ci)),
            id.Scopes,
            recentLogins,
            await StatusOfAsync(id, ct));
    }

    /// <summary>
    /// The state of the account behind the caller, so the panel can tell "waiting on an admin" from
    /// "this host does not know you" — two different sentences behind the same <c>none</c> tier.
    /// </summary>
    /// <remarks>
    /// A disabled account never reaches here: authority resolution ends its session at validation. An
    /// unreadable store answers <c>unknown</c> rather than picking one, because guessing here is
    /// guessing about somebody's access.
    /// </remarks>
    private async Task<string> StatusOfAsync(KgsmIdentity id, CancellationToken ct)
    {
        const string unknown = "unknown";
        if (!users.Available)
            return unknown;

        try
        {
            return (await users.Authority.ResolveAsync(id, ct)).User is { } account
                ? UserStatuses.ToWire(account.Status)
                : unknown;
        }
        catch (KgsmAuthProviderException)
        {
            return unknown;
        }
    }
}
