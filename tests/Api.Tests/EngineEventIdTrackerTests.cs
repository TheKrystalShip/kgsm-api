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
    private static EventWrapper Wrapper(string type = "server.started", string instance = "mc") => new()
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
    /// A line that carries its own name is identified by it, not by where it sits.
    /// </summary>
    /// <remarks>
    /// A position is right only while a segment is appended to and deleted whole; delete one line and
    /// every id after it becomes the id of a different event, with nothing to notice. The name
    /// survives that.
    /// </remarks>
    [Fact]
    public async Task OnRawEvent_UsesTheLinesOwnNameWhenItHasOne()
    {
        const string name = "01a016e9-d535-7b03-8a6a-b26ae718064c";

        var tracker = new EngineEventIdTracker();
        var position = new EventPosition("kgsm-watchdog", "2026-08-07.ndjson", 1234)
        {
            EventId = name,
        };

        await tracker.OnRawEvent(Wrapper(), position);

        Assert.Equal("evt_" + name, tracker.TakePendingId(NullLogger.Instance));
    }

    /// <summary>
    /// ⚠ The live push and the history read must derive one event's id identically.
    /// </summary>
    /// <remarks>
    /// This is the failure the tracker exists to prevent, and the only one that cannot be caught by
    /// looking at either side alone: a client reconciling a row it was pushed against the same event
    /// found in <c>/audit</c> sees two ids and reports two facts. Asserted against
    /// <see cref="AuditId.ForLine(string?, string, string, long)"/> itself — the single helper both
    /// paths call — rather than against a literal, so the two cannot drift apart while this still
    /// passes.
    /// </remarks>
    [Theory]
    [InlineData("01a016e9-d535-7b03-8a6a-b26ae718064c")]
    [InlineData(null)]
    public async Task OnRawEvent_AgreesWithTheHistoryReadsDerivation(string? eventId)
    {
        var tracker = new EngineEventIdTracker();
        var position = new EventPosition("kgsm-watchdog", "2026-08-07.ndjson", 1234)
        {
            EventId = eventId,
        };

        await tracker.OnRawEvent(Wrapper(), position);

        Assert.Equal(
            AuditId.ForLine(eventId, "kgsm-watchdog", "2026-08-07.ndjson", 1234),
            tracker.TakePendingId(NullLogger.Instance));
    }

    /// <summary>
    /// An id this ecosystem did not write is not trusted enough to name a row.
    /// </summary>
    /// <remarks>
    /// It cannot be assumed unique or ordered, and an audit id built on one would put a duplicate or a
    /// mis-sort into a page. Falling back to the position keeps the row addressable; reporting the
    /// producer is <c>envelope.event-id-shape</c>'s job, not this one's.
    /// </remarks>
    [Fact]
    public async Task OnRawEvent_FallsBackToThePositionForAMalformedName()
    {
        var tracker = new EngineEventIdTracker();

        await tracker.OnRawEvent(Wrapper(), new EventPosition("2026-08-07.ndjson", 1234)
        {
            EventId = "NOT-A-UUID",
        });

        Assert.Equal("evt_2026-08-07_000000001234", tracker.TakePendingId(NullLogger.Instance));
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
