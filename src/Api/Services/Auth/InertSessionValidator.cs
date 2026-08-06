using TheKrystalShip.KGSM.Auth.Sessions;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// The <c>Api__SessionsDisabled</c> posture: every session is alive, because there is no registry to
/// ask. Registered in place of the real validator when the switch is on, so nothing downstream needs
/// to know the difference — the bearer pipeline still calls a validator, it just always gets a yes.
/// </summary>
/// <remarks>
/// This is a stateless-JWT escape hatch for debugging, and it removes revocation entirely: a token
/// stays good until it expires on its own, whatever anyone does to the session behind it. Composing
/// it explicitly rather than branching inside the shared validator keeps that cost visible at the one
/// place the choice is made.
/// </remarks>
public sealed class InertSessionValidator : ISessionValidator
{
    public Task<bool> IsValidAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(true);

    public void Evict(string sessionId)
    {
        // Nothing is cached, so there is nothing to drop.
    }
}
