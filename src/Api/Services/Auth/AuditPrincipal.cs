using System.Security.Claims;

using TheKrystalShip.KGSM.Auth;

using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Derives the audit <em>actor</em> string to stamp onto a kgsm command for the current caller.
/// The command path passes this as <c>$KGSM_EVENT_ACTOR</c> via kgsm-lib; the engine echoes it on the
/// resulting event, and <c>AuditMapping.ParseActor</c> turns it back into the structured
/// <c>{kind,name,provider}</c>. The convention is <c>provider:name</c> (e.g. <c>discord:haru</c>).
/// </summary>
public static class AuditPrincipal
{
    /// <summary>
    /// <c>&lt;provider&gt;:&lt;username&gt;</c> for an authenticated identity (the human-readable handle,
    /// falling back to the provider's subject), or <see langword="null"/> when there is no resolvable principal —
    /// in which case the command is sent with no actor and kgsm applies its own honest OS-user fallback
    /// rather than the API fabricating an identity.
    /// </summary>
    public static string? ActorString(ClaimsPrincipal? user)
    {
        if (user?.Identity is not ClaimsIdentity ci || !ci.IsAuthenticated)
            return null;

        KgsmIdentity? id = SessionClaims.ReadIdentity(ci);
        if (id is null)
            return null;

        return id.ActorString;
    }
}
