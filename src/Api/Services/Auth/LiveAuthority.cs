using System.Security.Claims;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Auth.Users;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The account store could not be asked what a caller may do. Carried on the authentication failure
/// so the challenge answers <c>502</c> rather than <c>401</c>.
/// </summary>
/// <remarks>
/// The distinction is the point. A <c>401</c> tells a browser its session is no good and sends the
/// user back to sign in, which they cannot do either — every door reads the same file. A <c>502</c>
/// says the host cannot answer right now, which is what actually happened.
/// </remarks>
public sealed class AuthorityUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The caller's account has been switched off. A <c>401</c>: the session is over, not deferred.
/// </summary>
public sealed class AccountDisabledException(string message) : Exception(message);

/// <summary>
/// Resolves what the bearer of a valid session may do, on every request, from the account store.
/// </summary>
/// <remarks>
/// <para>
/// The token carries a <c>tier</c> claim minted at login, and this replaces it. Authorizing on the
/// claim would mean a demotion takes effect only when the token next rotates — up to its full life —
/// and it would mean this API and the assistant beside it, which re-derives authority per request,
/// disagree about the same person for that whole window. Resolving here collapses disable, demote and
/// revoke into one mechanism: the record is changed, and the next request reads the record.
/// </para>
/// <para>
/// The claim stays on the token as what it now is — a display hint the SPA can render before its
/// first call — and stops being what any gate trusts.
/// </para>
/// <para>
/// The cost is a lookup per request, which is why <see cref="UserStoreAuthority"/> caches for
/// <c>Api__AuthorityCacheSeconds</c>. That TTL is the demotion lag, and it is the only staleness left
/// in the model.
/// </para>
/// </remarks>
public sealed class LiveAuthority(UserDirectory users, ILogger<LiveAuthority> logger)
{
    /// <summary>
    /// Read the caller's identity off their validated token, resolve what it may do now, and write
    /// that back onto the principal.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the request may proceed at the tier now stamped on
    /// <paramref name="identity"/>; otherwise the reason it may not, to fail the authentication with.
    /// </returns>
    /// <remarks>
    /// A disabled account is the one outcome that ends the session rather than lowering it. Leaving it
    /// authenticated at <see cref="KgsmTier.None"/> would let someone who has been switched off keep
    /// reading their own profile and holding a live stream open; the switch is meant to be a door
    /// closing. An account that simply does not exist here is a stranger, and a stranger holds
    /// <see cref="KgsmTier.None"/> — which is a real answer, so their session stands and every gate
    /// refuses them.
    /// </remarks>
    public async Task<Exception?> ApplyAsync(ClaimsIdentity identity, CancellationToken ct)
    {
        if (SessionClaims.ReadIdentity(identity) is not { } caller)
            return new AuthorityUnavailableException("The bearer carries no identity to resolve.");

        if (!users.Available)
        {
            return new AuthorityUnavailableException(
                users.UnavailableReason ?? "The KGSM account store is unavailable on this host.");
        }

        AuthorityAnswer answer;
        try
        {
            answer = await users.Authority.ResolveAsync(caller, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not resolve authority for {Handle} from the account store.", caller.Handle);
            return new AuthorityUnavailableException("The KGSM account store could not be read.", e);
        }

        if (answer.Outcome == AuthorityOutcome.Disabled)
            return new AccountDisabledException($"The account behind {caller.Handle} is disabled.");

        return Stamp(identity, answer.Tier);
    }

    // Replace the minted tier with the resolved one, so every reader — the policy handler, /me, the
    // cluster vouch relay — sees one answer without knowing this ran.
    private static Exception? Stamp(ClaimsIdentity identity, KgsmTier tier)
    {
        foreach (Claim stale in identity.FindAll(KgsmAuthClaims.Tier).ToList())
        {
            // A claim can only be removed from the identity that owns it. A JWT's claims are owned by
            // the identity built from them, so this holds on the real pipeline.
            if (stale.Subject == identity)
                identity.RemoveClaim(stale);
        }

        // Refuse rather than add a second one. Claim readers take the first match, so a surviving
        // minted claim would silently win and this whole class would be doing nothing — the failure
        // it exists to prevent, arrived at by a different route.
        if (identity.FindFirst(KgsmAuthClaims.Tier) is not null)
            return new AuthorityUnavailableException(
                "The bearer's tier claim could not be replaced with the resolved one.");

        identity.AddClaim(new Claim(KgsmAuthClaims.Tier, KgsmTiers.ToWire(tier)));
        return null;
    }
}
