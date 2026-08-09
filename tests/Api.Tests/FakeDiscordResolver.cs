using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The test double for the sign-in seam — this is what makes the whole authorization surface testable
/// in-process WITHOUT reaching an identity provider. It switches purely on the OAuth <c>code</c> the
/// test passes, so there is no shared mutable state and the cases are parallel-safe:
/// <list type="bullet">
/// <item><c>viewer</c>/<c>operator</c>/<c>admin</c> → a verified identity at that tier.</item>
/// <item><c>none</c> → verified identity, no role (→ terminal 403).</item>
/// <item><c>bad</c> → null (the code couldn't be exchanged → 401).</item>
/// <item><c>boom</c> → throws <see cref="DiscordAuthException"/> (the provider unreachable → 502).</item>
/// </list>
/// <para>
/// It stands in for the whole composition rather than for either half alone, because the tier a case
/// wants is chosen by the <c>code</c> — which only the identity half ever sees. Splitting it would
/// mean carrying the choice from one call to the next in a field, and a shared mutable field is
/// exactly what makes a fake order-dependent.
/// </para>
/// </summary>
public sealed class FakeDiscordResolver : ISignInService
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

    private static Task<ResolvedPrincipal?> Resolve(string code) => code switch
    {
        "viewer" => Ok(KgsmTier.Viewer),
        "operator" => Ok(KgsmTier.Operator),
        "admin" => Ok(KgsmTier.Admin),
        "none" => Ok(KgsmTier.None),
        "boom" => throw new DiscordAuthException("simulated Discord outage"),
        _ => Task.FromResult<ResolvedPrincipal?>(null), // "bad" / anything else
    };

    private static Task<ResolvedPrincipal?> Ok(KgsmTier tier) =>
        Task.FromResult<ResolvedPrincipal?>(new ResolvedPrincipal(Identity, tier));
}
