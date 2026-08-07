using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="EngineEventIdTracker"/> — the raw-handler id capture that lets
/// <see cref="TheKrystalShip.Api.Services.Audit.KgsmAuditConsumer"/>'s typed handlers (which only ever
/// receive the typed <c>EventDataBase</c>, never the envelope or its position) tag a live-published row
/// with the SAME id a later <c>GET /audit</c> will give that event, so a client reconciling the two
/// sees one fact rather than two.
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

    /// <summary>
    /// The id comes from the journal position, which is exactly what the history read derives it
    /// from — so the live push and the stored row agree by construction, not by two computations
    /// happening to land on the same value.
    /// </summary>
    [Fact]
    public async Task OnRawEvent_ThenTakePendingId_IsTheIdOfThatJournalPosition()
    {
        var tracker = new EngineEventIdTracker();
        var position = new EventPosition("2026-08-07.ndjson", 1234);

        await tracker.OnRawEvent(Wrapper(), position);
        string id = tracker.TakePendingId(NullLogger.Instance);

        Assert.Equal(AuditId.ForPosition(position.Segment, position.Offset), id);
        Assert.Equal("evt_2026-08-07_000000001234", id);
    }

    /// <summary>
    /// Two identical events one second apart in the journal are still two events. A content-derived
    /// id cannot tell them apart — the engine's timestamps carry one-second granularity — so keying
    /// on position is what stops the second one from being dropped as a duplicate.
    /// </summary>
    [Fact]
    public async Task OnRawEvent_IdenticalEnvelopesAtDifferentPositions_GetDifferentIds()
    {
        var tracker = new EngineEventIdTracker();

        await tracker.OnRawEvent(Wrapper(), new EventPosition("2026-08-07.ndjson", 0));
        string first = tracker.TakePendingId(NullLogger.Instance);

        await tracker.OnRawEvent(Wrapper(), new EventPosition("2026-08-07.ndjson", 512));
        string second = tracker.TakePendingId(NullLogger.Instance);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// An envelope with no position has no addressable id, so it falls back rather than inventing a
    /// location. The fallback drives the live push only, never a stored row.
    /// </summary>
    [Fact]
    public async Task OnRawEvent_NoPosition_FallsBackRatherThanNamingAPositionItDoesNotHave()
    {
        var tracker = new EngineEventIdTracker();

        await tracker.OnRawEvent(Wrapper(), EventPosition.None);
        string id = tracker.TakePendingId(NullLogger.Instance);

        Assert.StartsWith("evt_", id);
        Assert.False(AuditId.TryParsePosition(id, out _, out _));
    }

    [Fact]
    public async Task TakePendingId_ClearsAfterTake_SoAStaleIdCanNeverBeReused()
    {
        var tracker = new EngineEventIdTracker();
        await tracker.OnRawEvent(Wrapper(), new EventPosition("2026-08-07.ndjson", 0));

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

        await tracker.OnRawEvent(Wrapper(instance: "mc"), new EventPosition("2026-08-07.ndjson", 0));
        string idA = tracker.TakePendingId(NullLogger.Instance);

        await tracker.OnRawEvent(Wrapper(instance: "factorio"), new EventPosition("2026-08-07.ndjson", 256));
        string idB = tracker.TakePendingId(NullLogger.Instance);

        Assert.NotEqual(idA, idB);
    }
}
