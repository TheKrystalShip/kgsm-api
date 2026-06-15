using System.Collections.Concurrent;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Commands;

/// <summary>
/// In-memory registry of command jobs (M3). Holds the authoritative copy of every job this process
/// created and enforces the <b>one-in-flight-per-server</b> invariant the gate relies on. Ephemeral
/// by design — a restart loses job history; durable persistence + the audit trail arrive at M5.
/// Thread-safe: the <see cref="CommandRunner"/> mutates from a background task while controllers
/// create/read from request threads.
/// </summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    // serverId -> the non-terminal job's id (present only while a job is queued/running there).
    private readonly ConcurrentDictionary<string, string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>The in-flight (queued/running) job for a server, or <c>null</c> if none.</summary>
    public Job? InFlightFor(string serverId) =>
        _inFlight.TryGetValue(serverId, out string? jobId) && _jobs.TryGetValue(jobId, out Job? job)
            ? job
            : null;

    /// <summary>
    /// Atomically claims the single in-flight slot for <paramref name="serverId"/> and records a new
    /// <see cref="JobState.Queued"/> job. Returns <c>null</c> if a job is already in flight for that
    /// server (the caller maps that to <c>409</c>). The slot is released by <see cref="Update"/> when
    /// the job reaches a terminal state — so the runner MUST always settle a started job.
    /// </summary>
    public Job? TryStart(string jobId, string serverId, string verb, DateTimeOffset createdAt)
    {
        // Claim the slot first; only register the job if we won the race.
        if (!_inFlight.TryAdd(serverId, jobId))
            return null;

        var job = new Job(jobId, serverId, verb, JobState.Queued, createdAt, SettledAt: null, Error: null);
        _jobs[jobId] = job;
        return job;
    }

    /// <summary>
    /// Stores a job state transition. On a terminal state (<see cref="JobState.Succeeded"/>/
    /// <see cref="JobState.Failed"/>) it releases the server's in-flight slot — but only if it still
    /// points at this job, so a newer job for the same server is never disturbed.
    /// </summary>
    public Job Update(Job job)
    {
        _jobs[job.Id] = job;
        if (job.State is JobState.Succeeded or JobState.Failed)
            _inFlight.TryRemove(new KeyValuePair<string, string>(job.ServerId, job.Id));
        return job;
    }

    /// <summary>The job by id, or <c>null</c> if this process never created/no-longer-holds it.</summary>
    public Job? Get(string jobId) => _jobs.TryGetValue(jobId, out Job? job) ? job : null;
}
