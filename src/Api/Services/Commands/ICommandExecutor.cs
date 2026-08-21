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
    /// <summary>Run the job and complete when it has settled, whatever the outcome. The job's own
    /// verdict is read back from <see cref="JobRegistry"/>; this returning is not itself a success.</summary>
    Task RunAsync(Job job, string? actor = null, string? origin = null);
}
