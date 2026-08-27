using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <c>JobRegistry</c>'s OBSERVED jobs — the record of a long-running engine operation this API did not
/// issue but saw kgsm start (an update run from the CLI, the assistant or the bot, via its
/// <c>server.update.started</c> event). They share the one-in-flight-per-server slot with issued jobs,
/// which is what makes "this instance is busy updating" one fact whoever started it.
/// </summary>
public sealed class JobRegistryObservedTests
{
    [Fact]
    public void ObservedJobClaimsTheSlotAsRunning()
    {
        var registry = new JobRegistry();

        Job? job = registry.TryStartObserved("job_engine1", "factorio-1", CommandVerb.Update, DateTimeOffset.UtcNow);

        Assert.NotNull(job);
        Assert.Equal(JobState.Running, job!.State);
        Assert.True(registry.IsObserved(job.Id));
        Assert.Equal(job.Id, registry.InFlightFor("factorio-1")?.Id);
    }

    [Fact]
    public void ObservedJobIsRefusedWhileAnIssuedJobHoldsTheSlot()
    {
        // The API issued the update itself; the engine event echoes the command it already tracks. Its own
        // job is the better record (actor, origin, and a settle driven by the process exiting), so the
        // observation mints nothing.
        var registry = new JobRegistry();
        Job? issued = registry.TryStart("job_issued1", "factorio-1", CommandVerb.Update, DateTimeOffset.UtcNow);

        Job? observed = registry.TryStartObserved("job_engine1", "factorio-1", CommandVerb.Update, DateTimeOffset.UtcNow);

        Assert.NotNull(issued);
        Assert.Null(observed);
        Assert.Equal("job_issued1", registry.InFlightFor("factorio-1")?.Id);
        Assert.False(registry.IsObserved("job_issued1"));
    }

    [Fact]
    public void SettlingAnObservedJobReleasesTheSlot()
    {
        var registry = new JobRegistry();
        Job job = registry.TryStartObserved("job_engine1", "factorio-1", CommandVerb.Update, DateTimeOffset.UtcNow)!;

        registry.Update(job with { State = JobState.Succeeded, SettledAt = DateTimeOffset.UtcNow });

        Assert.Null(registry.InFlightFor("factorio-1"));
    }

    [Fact]
    public void AnObservedJobWhoseRunNeverFinishedAgesOutOfTheSlot()
    {
        // kgsm emits its finish event on every outcome, so the only way the settle never arrives is the
        // engine being killed mid-run. The slot must not be held forever: it would freeze the surface on
        // "updating" AND make the gate refuse every later command for this server.
        var registry = new JobRegistry();
        registry.TryStartObserved("job_engine1", "factorio-1", CommandVerb.Update,
            DateTimeOffset.UtcNow.AddHours(-7));

        Assert.Null(registry.InFlightFor("factorio-1"));
        // The slot is genuinely free afterwards, not merely hidden from the read.
        Assert.NotNull(registry.TryStart("job_issued1", "factorio-1", CommandVerb.Start, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AnIssuedJobNeverAgesOut()
    {
        // Only observed jobs expire: an issued job is settled by the runner's finally, so a long-running
        // one is still genuinely running and must keep its slot.
        var registry = new JobRegistry();
        registry.TryStart("job_issued1", "factorio-1", CommandVerb.Update, DateTimeOffset.UtcNow.AddHours(-7));

        Assert.Equal("job_issued1", registry.InFlightFor("factorio-1")?.Id);
    }
}
