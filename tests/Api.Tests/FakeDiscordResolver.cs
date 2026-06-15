using TheKrystalShip.Api.Services.Auth;

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
public sealed class FakeDiscordResolver : IDiscordIdentityResolver
{
    public static readonly DiscordIdentity Identity =
        new("198772043", "haru", "haru", "https://cdn.discordapp.com/avatars/198772043/abc.png",
            ["identify", "guilds"]);

    public string BuildAuthorizeUrl(string state, string prompt) =>
        $"https://discord.test/authorize?state={state}&prompt={prompt}";

    public Task<ResolvedPrincipal?> ResolveAsync(string code, CancellationToken ct) => code switch
    {
        "viewer" => Ok(AuthTier.Viewer),
        "operator" => Ok(AuthTier.Operator),
        "admin" => Ok(AuthTier.Admin),
        "none" => Ok(AuthTier.None),
        "boom" => throw new DiscordAuthException("simulated Discord outage"),
        _ => Task.FromResult<ResolvedPrincipal?>(null), // "bad" / anything else
    };

    private static Task<ResolvedPrincipal?> Ok(AuthTier tier) =>
        Task.FromResult<ResolvedPrincipal?>(new ResolvedPrincipal(Identity, tier));
}
