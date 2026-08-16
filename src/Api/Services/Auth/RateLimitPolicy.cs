namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The names of this API's rate-limiting policies, so an endpoint and its registration cannot
/// disagree about which one it is on.
/// </summary>
public static class RateLimitPolicy
{
    /// <summary>
    /// The limiter every anonymous credential-touching endpoint runs under, partitioned per caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only throttle on the surface that is keyed on <em>who is calling</em>. The
    /// account store's lockout is exponential and effective, but it is keyed on the account being
    /// attacked, which leaves two gaps: spraying one password across many usernames costs an
    /// attacker nothing, and creating accounts has no account to lock out yet.
    /// </para>
    /// <para>
    /// The two throttles are deliberately different tools and both stay. Lockout protects one
    /// person's account from being guessed at; this protects the host from one caller, and its
    /// window is set so that a person who mistypes a password several times and then signs up
    /// never meets it.
    /// </para>
    /// </remarks>
    public const string Anonymous = "anonymous-auth";
}
