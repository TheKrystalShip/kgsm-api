using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// The M3 admissibility gate (architecture.html §5·d "state guards"). Deliberately <b>minimal</b>:
/// it rejects only the obvious no-ops against the <em>real observed</em> status — start-when-running,
/// stop-when-stopped — and lets everything else through, so the engine (kgsm → watchdog/Docker) stays
/// the single authority on what a verb does. The API never fabricates an admissibility rule kgsm does
/// not actually enforce; a subtler-but-impossible transition runs and surfaces as a job
/// <see cref="JobState.Failed"/> + the engine's real error. An <see cref="ServerStatus.Unknown"/>
/// status never blocks — we cannot honestly call a transition a no-op when we could not read the
/// current state. Permission gating (tiers, identity) lands at M4.
/// </summary>
public static class CommandGate
{
    /// <summary>
    /// Returns <c>null</c> when the verb is admissible against <paramref name="observedStatus"/>;
    /// otherwise a human-readable reason for the <c>409</c>.
    /// </summary>
    public static string? Inadmissible(string verb, string observedStatus) => verb switch
    {
        CommandVerb.Start when observedStatus == ServerStatus.Running => "server is already running",
        CommandVerb.Stop when observedStatus == ServerStatus.Stopped => "server is already stopped",
        _ => null,
    };
}
