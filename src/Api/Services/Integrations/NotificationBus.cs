using System.Threading.Channels;
using TheKrystalShip.Api.Contracts;

namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The in-process tap that turns the always-on audit flow into notifications (M8·c Increment B). Every
/// audit row — whatever its source (a kgsm event echo, a watchdog <c>system</c> crash, an API-internal
/// write) — is offered here by <see cref="Audit.AuditService.AppendAsync"/>; the bus keeps only the
/// notifiable ones (a catalog event) and hands them to <see cref="NotificationDeliveryWorker"/>.
/// </summary>
/// <remarks>
/// Tapping the audit append (not the <c>StreamHub</c>) is deliberate: the audit write is unconditional,
/// while the WS pumps are subscriber-gated — so a notification fires whether or not anyone is watching the
/// stream. And because the audit log is the single, no-double-write writer, one lifecycle action yields
/// exactly one notification (never two).
/// </remarks>
public interface INotificationBus
{
    /// <summary>Map an audit row to a notifiable event and enqueue it. A non-catalog action (the common
    /// case — <c>auth.*</c>, <c>network.*</c>, …) is dropped before it ever reaches the queue. <b>Never
    /// throws and never blocks</b> the caller (the audit write): the queue is bounded and drops on overflow
    /// (logged), because notifications are best-effort and must never back-pressure persistence.</summary>
    void Publish(AuditRecord record);

    /// <summary>The worker's drain. Completes when the channel completes (app stop).</summary>
    IAsyncEnumerable<NotificationEvent> ReadAllAsync(CancellationToken ct);
}

/// <inheritdoc cref="INotificationBus"/>
public sealed class NotificationBus : INotificationBus
{
    // Bounded so a stuck/slow worker can never grow memory without limit. DropOldest, not DropWrite: if we
    // must shed under a flood, a stale "online" matters less than the freshest "crashed". Every drop is
    // logged — silent loss would violate the honesty culture (we never pretend we delivered).
    private const int Capacity = 256;

    private readonly Channel<NotificationEvent> _channel;

    public NotificationBus(ILogger<NotificationBus> logger)
    {
        _channel = Channel.CreateBounded<NotificationEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,   // the one NotificationDeliveryWorker
                SingleWriter = false,  // appends can overlap (AuditService publishes after its write gate)
            },
            itemDropped: dropped => logger.LogWarning(
                "notification queue full ({Capacity}) — dropped {Action} for {Server} (event {Id}); "
                + "delivery is best-effort", Capacity, dropped.Action, dropped.ServerId ?? "(none)", dropped.AuditId));
    }

    public void Publish(AuditRecord record)
    {
        string? catalogId = NotificationCatalog.CatalogIdForAction(record.Action);
        if (catalogId is null) return; // not a notifiable action — dropped before the queue (no work, no read)

        var ev = new NotificationEvent(
            catalogId, record.Action, record.ServerId, record.Severity, record.Summary, record.Ts, record.Id);

        // Bounded + DropOldest → TryWrite always accepts (it evicts the oldest on overflow), so this never
        // blocks the audit write. The itemDropped callback above logs any eviction.
        _channel.Writer.TryWrite(ev);
    }

    public IAsyncEnumerable<NotificationEvent> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
