namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The Discord seam (M4·a). Everything that talks to <c>discord.com</c> lives behind this
/// interface, so the whole authorization surface — callback verdict, tier gating, the 401/403
/// matrix — is testable in-process with a fake, exactly as M3's gate-rejects-before-execution made
/// the command gate testable without mutation. The real <see cref="DiscordIdentityResolver"/> is
/// the only thing the live OAuth round-trip (M4·b) exercises.
/// </summary>
public interface IDiscordIdentityResolver
{
    /// <summary>
    /// The OAuth authorize URL to bounce the browser to (the <c>/auth/discord/start</c> target).
    /// <paramref name="state"/> is the opaque CSRF/round-trip token; <paramref name="prompt"/> is
    /// <c>none</c> (silent SSO) or <c>consent</c> (interactive fallback).
    /// </summary>
    string BuildAuthorizeUrl(string state, string prompt);

    /// <summary>
    /// Exchange an OAuth <paramref name="code"/> → verify identity (<c>/users/@me</c>) → resolve the
    /// guild role via the bot token → tier. Returns <c>null</c> when the exchange or identity step
    /// fails (the caller maps that to an error, never a default grant). A successful return may still
    /// carry <see cref="AuthTier.None"/> (verified identity, no role on this host → terminal 403).
    /// </summary>
    Task<ResolvedPrincipal?> ResolveAsync(string code, CancellationToken ct);
}
