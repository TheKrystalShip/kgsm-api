using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Captures the id of the kgsm engine event currently being dispatched, so a typed
/// <c>IEventService.RegisterHandler&lt;T&gt;</c> callback in <see cref="KgsmAuditConsumer"/> — which
/// only ever receives the typed <c>EventDataBase</c>, never the envelope or its position — can tag its
/// shaped, non-persisted audit row with the SAME id a later <c>GET /audit</c> will give that event.
/// Without this, a client reconciling a live-pushed <c>audit.append</c> row against the same event
/// found again in history would see two ids for one fact.
/// </summary>
/// <remarks>
/// The id is <see cref="AuditId.ForPosition(string, long)"/> over the journal position the transport reports, which
/// is what the history read derives it from too — so the two agree by construction rather than by two
/// computations happening to match. An envelope arriving with no position (a transport that cannot
/// supply one) has no addressable id and falls back below.
/// </remarks>
/// <remarks>
/// <b>Why a single mutable field is safe.</b> kgsm-lib reads the journal one line at a time, and each
/// envelope is processed end-to-end — raw handlers, then typed dispatch, both fully awaited — before
/// the next line is read (<c>EventService.OnEventReceivedAsync</c>). So "the raw handler that just
/// fired" and "the typed handler about to run" always refer to the same event, with no interleaving.
/// Register <see cref="OnRawEvent"/> via <c>IEventService.RegisterRawHandler</c> (raw handlers run
/// before typed dispatch, for every event, known or unknown), then call <see cref="TakePendingId"/>
/// from inside each typed handler that needs the id.
/// </remarks>
public sealed class EngineEventIdTracker
{
    private volatile string? _pendingId;

    /// <summary>Register this as an <c>IEventService</c> raw handler. Stashes the id for the envelope
    /// that was just received; never throws (an envelope with no position simply leaves nothing
    /// stashed, and <see cref="TakePendingId"/> falls back).</summary>
    public Task OnRawEvent(EventWrapper wrapper, EventPosition position)
    {
        // ⚠ Derived exactly the way the history read derives it, through the one shared helper. A
        // line's own name when it has one, the position when it does not — and the producer riding
        // into the positional form, because a byte offset alone names a different event in every
        // journal. Getting any of that out of step is invisible until a client reconciles a
        // live-pushed row against the same event found in history and sees two ids for one fact.
        _pendingId = position switch
        {
            { IsKnown: false } => null,
            { Producer: { Length: > 0 } producer } =>
                AuditId.ForLine(position.EventId, producer, position.Segment, position.Offset),
            _ => AuditId.ForLine(position.EventId, position.Segment, position.Offset),
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Consume the id captured for the in-flight event, clearing it so a later miss can never reuse a
    /// stale value. Falls back to a random <c>evt_</c> id (logged) on the defensive case that no raw
    /// handler ran first — an operational safety net only: this id drives the realtime push and the
    /// alert↔audit recovery bridge, never persistence (the journal is the record and the id read back
    /// from it is what the merged <c>/audit</c> history serves, so a fallback here can never desync a
    /// stored row).
    /// </summary>
    public string TakePendingId(ILogger logger)
    {
        string? id = _pendingId;
        _pendingId = null;
        if (id is not null)
            return id;

        id = "evt_" + Guid.NewGuid().ToString("N")[..16];
        logger.LogWarning(
            "Audit: no raw-handler id captured for this engine event — using fallback id {Id} "
            + "(realtime push / alert bridge only; never affects the persisted /audit history)", id);
        return id;
    }
}
