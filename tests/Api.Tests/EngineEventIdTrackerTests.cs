using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="EngineEventIdTracker"/> — the raw-handler id capture that lets
/// <see cref="TheKrystalShip.Api.Services.Audit.KgsmAuditConsumer"/>'s typed handlers (which only ever
/// receive the typed <c>EventDataBase</c>, never the raw <see cref="EventWrapper"/>) tag a live-published
/// row with the SAME deterministic id kgsm-monitor independently computes for the identical envelope
/// (event-history-plan.md Phase C).
/// </summary>
public sealed class EngineEventIdTrackerTests
{
    private static EventWrapper Wrapper(string type = "instance_started", string instance = "mc") => new()
    {
        EventType = type,
        Data = JsonSerializer.SerializeToElement(new { InstanceName = instance }),
        Timestamp = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
        Actor = "discord:haru",
        Origin = "ui",
        Hostname = "hotrod",
    };

    [Fact]
    public async Task OnRawEvent_ThenTakePendingId_MatchesAuditIdForEvent()
    {
        var tracker = new EngineEventIdTracker();
        EventWrapper wrapper = Wrapper();

        await tracker.OnRawEvent(wrapper);
        string id = tracker.TakePendingId(NullLogger.Instance);

        Assert.Equal(AuditId.ForEvent(wrapper), id);
        Assert.StartsWith("evt_", id);
    }

    [Fact]
    public async Task TakePendingId_ClearsAfterTake_SoAStaleIdCanNeverBeReused()
    {
        var tracker = new EngineEventIdTracker();
        await tracker.OnRawEvent(Wrapper());

        string first = tracker.TakePendingId(NullLogger.Instance);
        // Nothing pending now — a second take must NOT silently return the same (stale) id again.
        string second = tracker.TakePendingId(NullLogger.Instance);

        Assert.NotEqual(first, second);
        Assert.StartsWith("evt_", second); // still a well-formed fallback id, never null/throws
    }

    [Fact]
    public void TakePendingId_NoRawEventFired_FallsBackToARandomIdNeverThrows()
    {
        var tracker = new EngineEventIdTracker();

        string id = tracker.TakePendingId(NullLogger.Instance);

        Assert.False(string.IsNullOrEmpty(id));
        Assert.StartsWith("evt_", id);
    }

    [Fact]
    public async Task OnRawEvent_DifferentEnvelopes_ProduceDifferentIds()
    {
        var tracker = new EngineEventIdTracker();

        await tracker.OnRawEvent(Wrapper(instance: "mc"));
        string idA = tracker.TakePendingId(NullLogger.Instance);

        await tracker.OnRawEvent(Wrapper(instance: "factorio"));
        string idB = tracker.TakePendingId(NullLogger.Instance);

        Assert.NotEqual(idA, idB);
    }
}
