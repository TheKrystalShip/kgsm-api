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
    /// <summary>
    /// How long an engine-observed job may hold a server's in-flight slot without its matching
    /// finish event. kgsm emits <c>instance_update_finished</c> on every outcome, so the only way
    /// the settle never arrives is the engine being killed mid-run — and a slot held forever would
    /// both freeze the surface on "updating" and make the gate refuse every later command for that
    /// server. Generous enough to cover a genuinely long download-and-deploy, bounded so a killed
    /// run heals on its own.
    /// </summary>
    private static readonly TimeSpan EngineJobMaxAge = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    // serverId -> the non-terminal job's id (present only while a job is queued/running there).
    private readonly ConcurrentDictionary<string, string> _inFlight = new(StringComparer.Ordinal);
    // The ids of jobs this process OBSERVED rather than issued (see TryStartObserved). Only these
    // may be settled by an engine event, and only these age out.
    private readonly ConcurrentDictionary<string, byte> _observed = new(StringComparer.Ordinal);

    /// <summary>
    /// The in-flight (queued/running) job for a server, or <c>null</c> if none. An observed job past
    /// <see cref="EngineJobMaxAge"/> is released here rather than reported — expiry is lazy so the
    /// registry needs no timer, and every reader (the gate, the aggregator) sees the same answer.
    /// </summary>
    public Job? InFlightFor(string serverId)
    {
        if (!_inFlight.TryGetValue(serverId, out string? jobId) || !_jobs.TryGetValue(jobId, out Job? job))
            return null;

        if (_observed.ContainsKey(job.Id) && DateTimeOffset.UtcNow - job.CreatedAt > EngineJobMaxAge)
        {
            Update(job with
            {
                State = JobState.Failed,
                SettledAt = DateTimeOffset.UtcNow,
                Error = "the engine never reported this run finishing",
            });
            return null;
        }

        return job;
    }

    /// <summary>
    /// Records a long-running operation this API did <em>not</em> issue but observed the engine
    /// start (a kgsm update run from the CLI, the assistant or the bot — its
    /// <c>instance_update_started</c> event). It claims the same one-per-server slot as an issued
    /// job, so every surface reads one in-flight record whoever started it, and the gate refuses to
    /// stack a command on an instance kgsm is already busy with. Returns <c>null</c> when the slot
    /// is already taken — normally because this API issued the very command the event echoes, in
    /// which case its own job is the better record and the event needs no second one.
    /// </summary>
    public Job? TryStartObserved(string jobId, string serverId, string verb, DateTimeOffset startedAt)
    {
        Job? job = TryStart(jobId, serverId, verb, startedAt);
        if (job is null)
            return null;

        _observed[jobId] = 0;
        return Update(job with { State = JobState.Running });
    }

    /// <summary>Whether this job was observed rather than issued (see <see cref="TryStartObserved"/>).</summary>
    public bool IsObserved(string jobId) => _observed.ContainsKey(jobId);

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
    /// Stores a job state transition. On a terminal state (<see cref="JobState.IsTerminal"/>) it
    /// releases the server's in-flight slot — but only if it still points at this job, so a newer job
    /// for the same server is never disturbed.
    /// </summary>
    public Job Update(Job job)
    {
        _jobs[job.Id] = job;
        if (JobState.IsTerminal(job.State))
            _inFlight.TryRemove(new KeyValuePair<string, string>(job.ServerId, job.Id));
        return job;
    }

    /// <summary>The job by id, or <c>null</c> if this process never created/no-longer-holds it.</summary>
    public Job? Get(string jobId) => _jobs.TryGetValue(jobId, out Job? job) ? job : null;
}
