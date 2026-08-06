using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Discord;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The test double for the Discord seam — this is what makes the whole authorization surface
/// testable in-process WITHOUT discord.com (the M3-style "exercise the contract without the live
/// dependency" move). It switches purely on the OAuth <c>code</c> the test passes, so there is no
/// shared mutable state and the cases are parallel-safe:
/// <list type="bullet">
/// <item><c>viewer</c>/<c>operator</c>/<c>admin</c> → a verified identity at that tier.</item>
/// <item><c>none</c> → verified identity, no role (→ terminal 403).</item>
/// <item><c>bad</c> → null (the code couldn't be exchanged → 401).</item>
/// <item><c>boom</c> → throws <see cref="DiscordAuthException"/> (Discord unreachable → 502).</item>
/// </list>
/// </summary>
public sealed class FakeDiscordResolver : IDiscordDirectory
{
    public static readonly DiscordIdentity Identity =
        new("198772043", "haru", "haru", "https://cdn.discordapp.com/avatars/198772043/abc.png",
            ["identify", "guilds"]);

    public string BuildAuthorizeUrl(string state, string codeChallenge, string prompt) =>
        $"https://discord.test/authorize?state={state}&code_challenge={codeChallenge}&prompt={prompt}";

    /// <summary>
    /// Records the verifier the callback presented, so a test can assert the PKCE half of the
    /// handshake actually round-tripped rather than trusting that it was built.
    /// </summary>
    public string? LastCodeVerifier { get; private set; }

    /// <summary>
    /// Not reached by the login path — this surface resolves the tier inside <see cref="ResolveAsync"/>
    /// — so it answers "not a member" rather than pretending to a roster it does not have.
    /// </summary>
    public Task<IReadOnlyList<string>?> GetGuildRolesAsync(string userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

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
