using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// The single writer of the append-only audit log (M5) — persistence downstream of the stateless
/// engine (CLAUDE.md invariant #5). Every audit row, whatever its source (a kgsm lifecycle event via
/// <see cref="KgsmAuditConsumer"/>, or an API-internal action like auth), lands here; the service
/// assigns the public id, persists one row, and pushes the <c>audit.append</c> frame to the
/// <c>audit</c> WS topic. Reads go straight to <see cref="AuditQueries"/> on the request scope.
/// </summary>
/// <remarks>
/// <para><b>Singleton.</b> Writes can arrive off the request path (the event consumer's background
/// listener), so the service owns its own DI scope per write rather than capturing a request-scoped
/// <see cref="AppDbContext"/> (the same scope-per-unit pattern as the command runner).</para>
/// <para><b>Serialized.</b> SQLite is single-writer; <see cref="_writeGate"/> serializes appends so
/// concurrent events never collide on "database is locked". Audit volume is low (lifecycle actions),
/// so this is not a throughput concern.</para>
/// </remarks>
public sealed class AuditService(
    IServiceScopeFactory scopeFactory,
    StreamHub hub,
    ILogger<AuditService> logger)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private bool _ensured;

    /// <summary>
    /// Create the audit schema if it does not exist (idempotent). <c>EnsureCreated</c>, NOT a
    /// migration — see <see cref="AppDbContext"/>. Called once at startup (the consumer) so the table
    /// exists before <c>GET /audit</c> serves or any append runs, even when the engine is absent.
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

    /// <summary>Append one immutable audit row and announce it on the <c>audit</c> WS topic.</summary>
    public async Task<AuditRecord> AppendAsync(AuditWrite write, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);

        string id = "evt_" + Guid.NewGuid().ToString("N")[..10];
        AuditEntry entity = AuditMapping.ToEntity(write, id);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Audit.Add(entity);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }

        AuditRecord record = AuditMapping.ToRecord(entity);

        // Coalesce key = the unique event id, NOT a static "audit" key: audit appends are distinct,
        // immutable facts that must never supersede one another in a slow client's outbound queue
        // (a static key would silently drop all but the latest). A truly stalled client is still torn
        // down by the send timeout and re-hydrates via GET /audit on reconnect (§3·j).
        hub.Publish(StreamProtocol.AuditTopic, StreamProtocol.AuditEntityKey(id),
            new StreamMessage(StreamProtocol.AuditTopic, StreamProtocol.AuditAppend, record));

        logger.LogDebug("audit append {Id} {Action} actor={Actor} origin={Origin}",
            id, write.Action, write.Actor.Name, write.Origin ?? "(none)");
        return record;
    }
}
