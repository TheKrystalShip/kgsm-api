using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The test double for the sign-in seam — this is what makes the whole authorization surface testable
/// in-process WITHOUT reaching an identity provider. It switches purely on the OAuth <c>code</c> the
/// test passes, so there is no shared mutable state and the cases are parallel-safe:
/// <list type="bullet">
/// <item><c>viewer</c>/<c>operator</c>/<c>admin</c>/<c>none</c> → the standing verified identity.</item>
/// <item><c>as:&lt;subject&gt;</c> → a verified identity with that Discord subject, so a test can
/// choose <em>who</em> arrives and set up the account behind them.</item>
/// <item><c>bad</c> → null (the code couldn't be exchanged → 401).</item>
/// <item><c>boom</c> → throws <see cref="DiscordAuthException"/> (the provider unreachable → 502).</item>
/// </list>
/// <para>
/// It stands in for the whole composition rather than for either half alone, because who a case wants
/// to arrive as is chosen by the <c>code</c> — which only the identity half ever sees. Splitting it
/// would mean carrying the choice from one call to the next in a field, and a shared mutable field is
/// exactly what makes a fake order-dependent.
/// </para>
/// <para>
/// It serves the identity seam as well as the composed one, because linking an account uses the
/// identity half alone — there is no tier in that flow at all, which is the point of it.
/// </para>
/// <para>
/// The <b>tier</b> it reports is inert: authority comes from the account store, so what a login gets
/// is decided by the account the arriving identity proves and not by anything a provider says. The
/// tiered codes are kept because they read clearly at a call site, not because they still choose
/// anything.
/// </para>
/// </summary>
public sealed class FakeDiscordResolver : ISignInService, IIdentityProvider
{
    public static readonly KgsmIdentity Identity =
        new(KgsmActorProvider.Discord, "198772043", "haru", "haru",
            "https://cdn.discordapp.com/avatars/198772043/abc.png", ["identify", "guilds"]);

    public string Provider => KgsmActorProvider.Discord;

    public string BuildAuthorizeUrl(string state, string codeChallenge, string prompt) =>
        $"https://discord.test/authorize?state={state}&code_challenge={codeChallenge}&prompt={prompt}";

    /// <summary>
    /// Records the verifier the callback presented, so a test can assert the PKCE half of the
    /// handshake actually round-tripped rather than trusting that it was built.
    /// </summary>
    public string? LastCodeVerifier { get; private set; }

    public Task<ResolvedPrincipal?> ResolveAsync(string code, string codeVerifier, CancellationToken ct)
    {
        LastCodeVerifier = codeVerifier;
        return Resolve(code);
    }

    /// <summary>The identity half on its own — what attaching an account to an existing one uses.</summary>
    public async Task<KgsmIdentity?> VerifyAsync(string code, string codeVerifier, CancellationToken ct)
    {
        LastCodeVerifier = codeVerifier;
        return (await Resolve(code))?.Identity;
    }

    /// <summary>The OAuth code that arrives as <paramref name="subject"/> rather than as the standing
    /// identity — so a test can set up the account behind whoever it wants to walk in.</summary>
    public static string CodeFor(string subject) => "as:" + subject;

    /// <summary>The identity <see cref="CodeFor"/> produces, for the account setup that precedes it.</summary>
    public static KgsmIdentity IdentityFor(string subject) =>
        Identity with { Subject = subject, Username = "user" + subject, Display = "User " + subject };

    private static Task<ResolvedPrincipal?> Resolve(string code)
    {
        if (code.StartsWith("as:", StringComparison.Ordinal))
            return Ok(IdentityFor(code[3..]), KgsmTier.Viewer);

        return code switch
        {
            "viewer" => Ok(Identity, KgsmTier.Viewer),
            "operator" => Ok(Identity, KgsmTier.Operator),
            "admin" => Ok(Identity, KgsmTier.Admin),
            "none" => Ok(Identity, KgsmTier.None),
            "boom" => throw new DiscordAuthException("simulated Discord outage"),
            _ => Task.FromResult<ResolvedPrincipal?>(null), // "bad" / anything else
        };
    }

    private static Task<ResolvedPrincipal?> Ok(KgsmIdentity identity, KgsmTier tier) =>
        Task.FromResult<ResolvedPrincipal?>(new ResolvedPrincipal(identity, tier));
}
