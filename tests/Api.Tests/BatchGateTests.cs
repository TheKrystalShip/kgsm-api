using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The two rules a batch adds to the command path that a single command never exercised: a job can now
/// sit <see cref="JobState.Queued"/> for a long time, and a job can end without ever having run.
/// </summary>
public sealed class BatchGateTests
{
    [Fact]
    public void Busy_NamesTheVerb_AndSaysItHasNotStarted()
    {
        var queued = new Job("job_1", "srv-a", CommandVerb.Stop, JobState.Queued,
            DateTimeOffset.UtcNow, null, null);

        string reason = CommandGate.Busy(queued);

        // A refusal has to name the action it is waiting for, or the caller re-sends the same command.
        // "In flight" would describe queued work as under way, which is a different thing to wait for.
        Assert.Contains("stop", reason);
        Assert.Contains("queued", reason);
        Assert.DoesNotContain("already running", reason);
    }

    [Fact]
    public void Busy_ForRunningWork_SaysItIsRunning()
    {
        var running = new Job("job_1", "srv-a", CommandVerb.Update, JobState.Running,
            DateTimeOffset.UtcNow, null, null);

        string reason = CommandGate.Busy(running);

        Assert.Contains("update", reason);
        Assert.Contains("already running", reason);
    }

    [Fact]
    public void Busy_WithNoJob_StillRefusesWithoutInventingOne()
    {
        // The slot was released between the failed claim and the read. Say the true, general thing
        // rather than naming a job that is no longer there.
        Assert.Contains("in flight", CommandGate.Busy(null));
    }

    [Fact]
    public void Cancelled_IsTerminal_SoTheServersSlotIsReleased()
    {
        // The registry releases a server's one in-flight slot on any terminal state. A cancelled job
        // that did not count as terminal would hold that server's slot forever, and every later command
        // for it would be refused against work that will never run.
        Assert.True(JobState.IsTerminal(JobState.Cancelled));
        Assert.True(JobState.IsTerminal(JobState.Succeeded));
        Assert.True(JobState.IsTerminal(JobState.Failed));
        Assert.False(JobState.IsTerminal(JobState.Queued));
        Assert.False(JobState.IsTerminal(JobState.Running));
    }

    [Fact]
    public void QueuedJob_HoldsTheServersInFlightSlot()
    {
        var registry = new JobRegistry();
        Job? first = registry.TryStart("job_1", "srv-a", CommandVerb.Stop, DateTimeOffset.UtcNow);
        Assert.NotNull(first);
        Assert.Equal(JobState.Queued, first!.State);

        // Batch members are created queued at accept, so a competing command must be refused against
        // work that is merely waiting — a manual start must not race a stop already committed to.
        Assert.Null(registry.TryStart("job_2", "srv-a", CommandVerb.Start, DateTimeOffset.UtcNow));

        registry.Update(first with { State = JobState.Cancelled, SettledAt = DateTimeOffset.UtcNow });
        Assert.NotNull(registry.TryStart("job_3", "srv-a", CommandVerb.Start, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MemberTerminality_MatchesWhatTheWorkerWillPickUp()
    {
        // The worker only ever picks up `pending`; everything the batch settles on must be terminal or
        // the batch can never close.
        Assert.False(BatchMemberState.IsTerminal(BatchMemberState.Pending));
        Assert.False(BatchMemberState.IsTerminal(BatchMemberState.Running));
        Assert.True(BatchMemberState.IsTerminal(BatchMemberState.Succeeded));
        Assert.True(BatchMemberState.IsTerminal(BatchMemberState.Failed));
        Assert.True(BatchMemberState.IsTerminal(BatchMemberState.Refused));
        Assert.True(BatchMemberState.IsTerminal(BatchMemberState.Cancelled));
        Assert.True(BatchMemberState.IsTerminal(BatchMemberState.Unknown));
    }
}
