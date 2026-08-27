using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Integrations;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Announces audit rows on the realtime topic and the notification bus, and owns the schema of the
/// local table that holds this host's pre-cutover history.
/// </summary>
/// <remarks>
/// <para>
/// <b>It writes nothing.</b> Every producer on this host — the engine, each leaf, and this API — now
/// records what it did in its own event journal, and <see cref="AuditQueries"/> serves the merge. A
/// row reaches a reader by being read back off a journal, so there is no append path here to keep
/// consistent with one, and no second writer that could disagree with a producer about its own
/// actions.
/// </para>
/// <para>
/// The local table stays because it holds real history: every row written before each producer
/// started keeping its own journal. It is read and never added to.
/// </para>
/// </remarks>
public sealed class AuditService(
    IServiceScopeFactory scopeFactory,
    StreamHub hub,
    INotificationBus notifications,
    ILogger<AuditService> logger)
{
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>
    /// Create the audit schema if it does not exist (idempotent). <c>EnsureCreated</c>, NOT a
    /// migration — see <see cref="AppDbContext"/>. Called once at startup (the consumer) so the table
    /// exists before <c>GET /audit</c> reads this host's pre-cutover history out of it, even when the
    /// engine is absent.
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (_ensured) return;
        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ensured) return;
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
            _ensured = true;
        }
        finally { _ensureGate.Release(); }
    }

    /// <summary>
    /// Announce an already-shaped <paramref name="record"/> on the <c>audit</c> WS topic + the
    /// notification bus, WITHOUT persisting it. The kgsm engine's own history now lives in
    /// the engine's journal — <c>GET /audit</c> merges it in at read time via
    /// <see cref="EngineEventShaping"/>, so <see cref="KgsmAuditConsumer"/> no longer writes these rows
    /// to the local table. Live consumption (realtime SSE + Discord/Slack notifications) stays exactly
    /// as it was: this is the same tail <see cref="AppendAsync"/> runs after a successful write, just
    /// without the write. <paramref name="record"/> must already carry its final id — the deterministic
    /// <c>AuditId.ForEvent</c> value (via <see cref="EngineEnvelopeTracker"/>), matching what the monitor
    /// independently computed for the SAME envelope — so a client reconciling this live push against a
    /// later paginated <c>GET /audit</c> page (which shapes the monitor's persisted copy of the same
    /// event) sees one identity, not two.
    /// </summary>
    public void PublishLive(AuditRecord record) => Publish(record, persisted: false);

    private void Publish(AuditRecord record, bool persisted)
    {
        // Coalesce key = the unique event id, NOT a static "audit" key: audit appends are distinct,
        // immutable facts that must never supersede one another in a slow client's outbound queue
        // (a static key would silently drop all but the latest). A truly stalled client is still torn
        // down by the send timeout and re-hydrates via GET /audit on reconnect (§3·j).
        // Two shapes of the same row, because two tiers may be reading the topic. A viewer's live frame
        // has to match the page they hydrate from — a value withheld on refresh but pushed live is the
        // same value published, with a delay. Only the audit topic needs this: it is the only one whose
        // payload carries values the engine classifies as personal or privileged.
        AuditRecord forViewer = AuditRedaction.ForViewer(record);
        hub.Publish(StreamProtocol.AuditTopic, StreamProtocol.AuditEntityKey(record.Id),
            new StreamMessage(StreamProtocol.AuditTopic, StreamProtocol.AuditAppend, record),
            ReferenceEquals(forViewer, record)
                ? null
                : new StreamMessage(StreamProtocol.AuditTopic, StreamProtocol.AuditAppend, forViewer));

        // Increment B (M8·c): tap the ALWAYS-ON audit path so notifications fire on every kgsm lifecycle
        // event regardless of WS subscribers (the StreamHub pumps are subscriber-gated; this publish is
        // not). The bus keeps only catalog-mapped actions and the delivery worker routes them; a
        // non-notifiable action (auth.*, network.*, …) is dropped there. Never throws / never blocks.
        // Runs identically whether or not the row was persisted — a Discord crash notification must
        // still fire for an engine event the API no longer stores locally.
        notifications.Publish(record);

        logger.LogDebug("audit {Verb} {Id} {Action} actor={Actor} origin={Origin}",
            persisted ? "append" : "publish(live-only)", record.Id, record.Action, record.Actor.Name,
            record.Origin ?? "(none)");
    }
}
