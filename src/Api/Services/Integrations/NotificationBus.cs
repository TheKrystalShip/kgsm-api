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

    /// <summary>
    /// How recent a threshold row has to be to be worth announcing. The recorder transcribes the monitor's
    /// episodes and looks back a day on a cold start, so a host whose audit DB was wiped would otherwise
    /// wake every phone on the fleet with yesterday's weather.
    /// </summary>
    private static readonly TimeSpan ThresholdNoticeWindow = TimeSpan.FromMinutes(10);

    public void Publish(AuditRecord record)
    {
        string? catalogId = NotificationCatalog.CatalogIdForAction(record.Action);
        if (catalogId is null) return; // not a notifiable action — dropped before the queue (no work, no read)
        if (!IsWorthAnnouncing(record)) return;

        var ev = new NotificationEvent(
            catalogId, record.Action, record.ServerId, record.Severity, record.Summary, record.Ts, record.Id,
            SubjectKey: SubjectKeyOf(record));

        // Bounded + DropOldest → TryWrite always accepts (it evicts the oldest on overflow), so this never
        // blocks the audit write. The itemDropped callback above logs any eviction.
        _channel.Writer.TryWrite(ev);
    }

    public IAsyncEnumerable<NotificationEvent> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    /// <summary>
    /// The second gate, and it only ever narrows threshold rows — every other notifiable action is written
    /// the moment it happens and means what it says.
    /// <para>
    /// <b>An episode that ended without recovering is not an all-clear.</b> The monitor closes one as
    /// <c>unwatched</c> when an admin retunes or disables its rule, and as <c>interrupted</c> when the daemon
    /// itself restarts. Announcing either would be reporting a measurement nobody took — and neither loses
    /// anything: the person who retuned the rule is the one who did it, and a condition still true after a
    /// restart opens a fresh episode within seconds and announces itself as a breach.
    /// </para>
    /// <para>
    /// <b>And history is not news.</b> These rows are transcribed from the monitor's own durable episodes on
    /// a poll, so their timestamp is when the condition changed rather than when this process learned of it.
    /// A row older than <see cref="ThresholdNoticeWindow"/> is being caught up on, and a phone buzzing about
    /// it would be telling somebody about a temperature from last night.
    /// </para>
    /// </summary>
    private static bool IsWorthAnnouncing(AuditRecord record)
    {
        if (record.Action is not (AuditAction.HostThresholdBreach or AuditAction.HostThresholdClear)) return true;

        if (record.Action == AuditAction.HostThresholdClear
            && record.Meta is not null
            && record.Meta.TryGetValue("reason", out string? reason)
            && reason is "unwatched" or "interrupted")
            return false;

        return DateTimeOffset.UtcNow - record.Ts <= ThresholdNoticeWindow;
    }

    /// <summary>
    /// What the delivery worker coalesces repeats against. For nearly every action that is the server, and
    /// returning null says so. A threshold row is the exception: one host watches several conditions at once
    /// and they all carry a null server, so the subject is the watched condition — the rule plus the sensor
    /// or filesystem it names. Read off the row's own meta, which the recorder writes from the monitor's
    /// episode; a row missing those keys falls back to the server like everything else.
    /// </summary>
    private static string? SubjectKeyOf(AuditRecord record)
    {
        if (record.Action is not (AuditAction.HostThresholdBreach or AuditAction.HostThresholdClear)) return null;
        if (record.Meta is null) return null;
        record.Meta.TryGetValue("ruleKey", out string? rule);
        record.Meta.TryGetValue("ref", out string? reference);
        if (string.IsNullOrEmpty(rule) && string.IsNullOrEmpty(reference)) return null;
        return $"{rule}/{reference}/{record.ServerId}";
    }
}
