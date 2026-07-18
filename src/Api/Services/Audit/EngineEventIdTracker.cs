using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Captures the deterministic <see cref="AuditId.ForEvent"/> id for the kgsm engine event currently
/// being dispatched, so a typed <c>IEventService.RegisterHandler&lt;T&gt;</c> callback in
/// <see cref="KgsmAuditConsumer"/> — which only ever receives the typed <c>EventDataBase</c>, never the
/// raw <see cref="EventWrapper"/> (kgsm-lib has no combined-callback overload) — can still tag its
/// shaped, non-persisted audit row with the SAME id kgsm-monitor independently computed for the
/// identical envelope (event-history-plan.md Phase C). Without this, a client reconciling a
/// live-pushed <c>audit.append</c> row against the SAME event later returned by <c>GET /audit</c>
/// (shaped from the monitor's persisted copy) would see two different ids for one fact.
/// </summary>
/// <remarks>
/// <b>Why a single mutable field is safe.</b> kgsm-lib's socket listener accepts and fully drains one
/// connection at a time (<c>UnixSocketClient.AcceptConnectionsAsync</c> awaits
/// <c>HandleClientConnectionAsync</c> to completion before accepting the next), and within a
/// connection each envelope is processed end-to-end — raw handlers, then typed dispatch, both fully
/// awaited — before the next message is read (<c>EventService.OnEventReceivedAsync</c>). So "the raw
/// handler that just fired" and "the typed handler about to run" always refer to the exact same event,
/// with no interleaving from a concurrent event. Register <see cref="OnRawEvent"/> via
/// <c>IEventService.RegisterRawHandler</c> (raw handlers run before typed dispatch, for every event,
/// known or unknown), then call <see cref="TakePendingId"/> from inside each typed handler that needs
/// the id.
/// </remarks>
public sealed class EngineEventIdTracker
{
    private volatile string? _pendingId;

    /// <summary>Register this as an <c>IEventService</c> raw handler. Stashes the deterministic id for
    /// the envelope that was just received; never throws (a bad wrapper simply leaves nothing stashed,
    /// and <see cref="TakePendingId"/> falls back).</summary>
    public Task OnRawEvent(EventWrapper wrapper)
    {
        _pendingId = AuditId.ForEvent(wrapper);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Consume the id captured for the in-flight event, clearing it so a later miss can never reuse a
    /// stale value. Falls back to a random <c>evt_</c> id (logged) on the defensive case that no raw
    /// handler ran first — an operational safety net only: this id drives the realtime push and the
    /// alert↔audit recovery bridge, never persistence (the monitor's own independently-computed id is
    /// the source of truth for the merged <c>/audit</c> history, so a fallback here can never desync a
    /// persisted row).
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
