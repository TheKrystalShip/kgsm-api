using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// The M3 admissibility gate (architecture.html §5·d "state guards"). Deliberately <b>minimal</b>:
/// it rejects only the obvious no-ops against the <em>real observed</em> status — start-when-running,
/// start-when-starting, stop-when-stopped — and lets everything else through, so the engine (kgsm →
/// watchdog/Docker) stays the single authority on what a verb does. The API never fabricates an
/// admissibility rule kgsm does not actually enforce; a subtler-but-impossible transition runs and
/// surfaces as a job <see cref="JobState.Failed"/> + the engine's real error. An
/// <see cref="ServerStatus.Unknown"/> status never blocks — we cannot honestly call a transition a
/// no-op when we could not read the current state. Permission gating (tiers, identity) lands at M4.
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
        // A `starting` server already has a start in flight (the process spawned, just not yet confirmed
        // booted) — a second start against it is exactly the same no-op class as start-when-running, so
        // it gets the same synchronous 409 rather than racing the engine on an instance mid-boot.
        CommandVerb.Start when observedStatus == ServerStatus.Starting => "server is already starting",
        CommandVerb.Stop when observedStatus == ServerStatus.Stopped => "server is already stopped",
        // Stop IS admissible against `starting` on purpose (no case here) — an operator must be able to
        // abort a server that is stuck mid-boot; kgsm's stop path doesn't care whether the process finished
        // booting, only that it's up.
        //
        // kgsm refuses to update a RUNNING instance (the files are in use) — surface that synchronously as
        // a 409 against the observed status, rather than accepting a job that is bound to fail. `starting`
        // has the exact same files-in-use problem (the process is up, just not confirmed booted yet), so it
        // is blocked too. Unknown status never blocks (we cannot honestly call it a no-op), and a stopped
        // server is admissible; the engine refusal remains the backstop for any subtler case the observed
        // status cannot see.
        CommandVerb.Update when observedStatus is ServerStatus.Running or ServerStatus.Starting
            => "server must be stopped before updating",
        _ => null,
    };

    /// <summary>
    /// Why a server that already holds the registry's in-flight slot cannot take another command.
    /// </summary>
    /// <remarks>
    /// The reason <b>names the verb and says whether it has started</b>. Since a batch creates every
    /// member's job when it is accepted, the slot is now routinely held by work that is merely
    /// waiting, and "a command is already in flight" describes that wrongly — it reads as something
    /// under way, which invites the caller to wait for a run that has not begun. One helper so the
    /// single-command path and the batch preflight cannot word the same refusal differently.
    /// </remarks>
    public static string Busy(Contracts.Job? existing) => existing is null
        ? "a command is already in flight for this server"
        : existing.State == JobState.Queued
            ? $"a {existing.Verb} is queued for this server (job {existing.Id})"
            : $"a {existing.Verb} is already running on this server (job {existing.Id})";
}
