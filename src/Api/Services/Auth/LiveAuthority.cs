using System.Security.Claims;

using TheKrystalShip.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The replica could not be asked who a caller is. Carried on the authentication failure so the
/// challenge answers <c>502</c> rather than <c>401</c>.
/// </summary>
/// <remarks>
/// The distinction is the point. A <c>401</c> tells a browser its session is no good and sends the user
/// back to sign in, which changes nothing — this node reads the same replica afterwards. A <c>502</c> says
/// the host cannot answer right now, which is what actually happened.
/// </remarks>
public sealed class AuthorityUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The caller's account has been switched off. A <c>401</c>: the session is over, not deferred.
/// </summary>
public sealed class AccountDisabledException(string message) : Exception(message);

/// <summary>
/// Finds the account behind a valid session, on every request, in this node's replica, and stamps its
/// id on the principal for every gate after it.
/// </summary>
/// <remarks>
/// <para>
/// The token names who; nothing on it says what. What the account may do is evaluated per action from
/// the same replica (<see cref="NodeAccess"/>), so a change the anchor makes lands on the next request
/// with no session ended, and this API and the surfaces beside it cannot disagree about a person.
/// </para>
/// <para>
/// Three outcomes, kept apart: a disabled account fails authentication; an identity no account holds is
/// a stranger — authenticated, stamped with nothing, refused every action; an unreadable replica is an
/// outage, never a denial.
/// </para>
/// </remarks>
public sealed class LiveAuthority(MemberAccess access, ILogger<LiveAuthority> logger)
{
    /// <summary>
    /// Resolve the caller behind <paramref name="identity"/> and stamp the account on it.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the request may proceed; otherwise the reason it may not, to fail the
    /// authentication with.
    /// </returns>
    public async Task<Exception?> ApplyAsync(ClaimsIdentity identity, CancellationToken ct)
    {
        MemberAccessCaller caller = await access.ResolveAsync(new ClaimsPrincipal(identity), ct).ConfigureAwait(false);

        switch (caller.Refusal)
        {
            case MemberAccessRefusal.Unavailable:
                logger.LogError("Could not resolve a caller: the authority replica is unavailable ({Reason}).", caller.Reason);
                return new AuthorityUnavailableException(caller.Reason ?? "This node's authority replica could not be read.");

            case MemberAccessRefusal.AccountDisabled:
                return new AccountDisabledException("The account behind this session is disabled.");

            case MemberAccessRefusal.None:
                return Stamp(identity, caller.AccountId!);

            default:
                return null;
        }
    }

    // One account claim, the resolved one. A second would be read by whichever reader took the first match.
    private static Exception? Stamp(ClaimsIdentity identity, string accountId)
    {
        foreach (Claim stale in identity.FindAll(AccessClaims.Account).ToList())
        {
            if (stale.Subject == identity)
                identity.RemoveClaim(stale);
        }

        if (identity.FindFirst(AccessClaims.Account) is not null)
            return new AuthorityUnavailableException("The bearer carried an account claim that could not be replaced.");

        identity.AddClaim(new Claim(AccessClaims.Account, accountId));
        return null;
    }
}
