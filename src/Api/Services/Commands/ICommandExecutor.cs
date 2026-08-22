using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// Running one admitted job to completion — the single thing <see cref="BatchWorker"/> needs from
/// <see cref="CommandRunner"/>.
/// </summary>
/// <remarks>
/// The seam exists so the worker's <b>concurrency window can be tested without running anything</b>.
/// That window is the only thing bounding how much work this host takes on at once, and proving it
/// holds by starting four real game servers costs the host four game servers' worth of memory — which
/// is the failure the window exists to prevent. A fake executor that blocks until released makes the
/// property provable at no cost, and is the only reason this interface is not simply
/// <see cref="CommandRunner"/>.
/// </remarks>
public interface ICommandExecutor
{
    /// <summary>
    /// Run the job and complete when it has settled, whatever the outcome. The job's own verdict is read
    /// back from <see cref="JobRegistry"/>; this returning is not itself a success.
    /// </summary>
    /// <returns>
    /// The engine's exit code when the verb failed, else null. It is what lets a caller categorise a
    /// failure — a capacity refusal is not a fault — against a number the engine defined, rather than
    /// matching prose that is free to be reworded.
    /// </returns>
    /// <param name="force">Override the engine's node-capacity check — <c>start</c> only, and the
    /// batch's own answer for every member it dispatches. A batch is one decision applied N times, so
    /// the override is the batch's rather than each member's: an operator who judged a requirement
    /// overstated judged it for the selection they made.</param>
    Task<int?> RunAsync(Job job, string? actor = null, string? origin = null, bool force = false);
}
