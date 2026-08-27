using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Holds what the kgsm engine envelope currently being dispatched says, for a typed
/// <c>IEventService.RegisterHandler&lt;T&gt;</c> callback in <see cref="KgsmAuditConsumer"/> — which
/// only ever receives the typed <c>EventDataBase</c>, never the envelope, its position, or anything
/// the producer stamped beside the payload.
/// </summary>
/// <remarks>
/// Two things ride on this, and both are the same failure: one fact appearing as two.
/// <list type="bullet">
/// <item>The <b>id</b>, so a live-pushed <c>audit.append</c> row and the same event found again in
/// history carry one identity rather than two.</item>
/// <item>The <b>severity, outcome and summary the producer stamped</b>, so the row pushed the moment
/// it happened and the row read back an hour later say the same thing. Without them the live row
/// falls back to the type-derived default and a stop arrives grey, turning amber only when somebody
/// reloads the page.</item>
/// </list>
/// </remarks>
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
/// before typed dispatch, for every event, known or unknown), then call <see cref="TakePending"/>
/// from inside each typed handler that needs the id.
/// </remarks>
public sealed class EngineEnvelopeTracker
{
    private PendingEnvelope? _pending;

    /// <summary>Register this as an <c>IEventService</c> raw handler. Stashes what the envelope just
    /// received says; never throws (an envelope with no position simply leaves no id, and
    /// <see cref="TakePending"/> falls back).</summary>
    public Task OnRawEvent(EventWrapper wrapper, EventPosition position)
    {
        ArgumentNullException.ThrowIfNull(wrapper);

        // ⚠ Derived exactly the way the history read derives it, through the one shared helper. A
        // line's own name when it has one, the position when it does not — and the producer riding
        // into the positional form, because a byte offset alone names a different event in every
        // journal. Getting any of that out of step is invisible until a client reconciles a
        // live-pushed row against the same event found in history and sees two ids for one fact.
        string? id = position switch
        {
            { IsKnown: false } => null,
            { Producer: { Length: > 0 } producer } =>
                AuditId.ForLine(position.EventId, producer, position.Segment, position.Offset),
            _ => AuditId.ForLine(position.EventId, position.Segment, position.Offset),
        };

        _pending = new PendingEnvelope(id, wrapper.Severity, wrapper.Outcome, wrapper.Summary);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Consume what was captured for the in-flight event, clearing it so a later miss can never reuse
    /// stale values. The id falls back to a random <c>evt_</c> one (logged) on the defensive case that
    /// no raw handler ran first — an operational safety net only: it drives the realtime push and the
    /// alert↔audit recovery bridge, never persistence (the journal is the record and the id read back
    /// from it is what the merged <c>/audit</c> history serves, so a fallback here can never desync a
    /// stored row). The producer's own facts have no fallback and stay null, which reads as a producer
    /// that said nothing rather than as one that said something ordinary.
    /// </summary>
    public PendingEnvelope TakePending(ILogger logger)
    {
        PendingEnvelope? pending = _pending;
        _pending = null;

        if (pending?.Id is { Length: > 0 })
            return pending;

        string id = "evt_" + Guid.NewGuid().ToString("N")[..16];
        logger.LogWarning(
            "Audit: no raw-handler id captured for this engine event — using fallback id {Id} "
            + "(realtime push / alert bridge only; never affects the persisted /audit history)", id);
        return (pending ?? new PendingEnvelope(null, null, null, null)) with { Id = id };
    }
}

/// <summary>What the in-flight envelope said that its typed payload does not carry.</summary>
/// <param name="Id">The row's identity, or null until <see cref="EngineEnvelopeTracker.TakePending"/>
/// supplies a fallback.</param>
/// <param name="Severity">The weight the producer stamped, or null when it stamped none.</param>
/// <param name="Outcome">How the producer said it went, or null.</param>
/// <param name="Summary">The producer's own sentence, or null.</param>
public sealed record PendingEnvelope(string? Id, string? Severity, string? Outcome, string? Summary);
