using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="AuditQueries.PageMergedAsync"/> — the merge reader: a
/// ts-DESC page over {local API-only rows} ∪ {kgsm-monitor's shaped engine rows}. Pure unit tests against
/// a real (file-backed) SQLite <see cref="AppDbContext"/> and a hand-rolled <see cref="IEventJournalHistory"/>
/// fake — no live kgsm/monitor, no WebApplicationFactory (mirrors <c>OutboxDrainerTests.NewDb</c>'s
/// direct-EF pattern).
/// </summary>
public sealed class AuditMergeTests : IDisposable
{
    private const string HostId = "h1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"audittest-{Guid.NewGuid():N}.db");
    private readonly AppDbContext _db;

    public AuditMergeTests()
    {
        var provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"))
            .BuildServiceProvider();
        _db = provider.GetRequiredService<AppDbContext>();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }

    private async Task SeedLocalAsync(string action, DateTimeOffset ts, string id, string? serverId = "mc")
    {
        AuditWrite write = new(ts, "ui", new AuditActor(ActorKind.User, "haru", ActorProvider.Discord),
            action, AuditSeverity.Info, serverId is null ? null : new AuditTarget(AuditTargetKind.Server, serverId, serverId),
            serverId, HostId, $"{action} {serverId}", null);
        _db.Audit.Add(AuditMapping.ToEntity(write, id));
        await _db.SaveChangesAsync();
    }

    private static EventHistoryEntry EngineEvent(string id, DateTimeOffset ts, string type = "instance_started", string instance = "mc") =>
        new(id, ts, type, instance, null, null, null, null,
            System.Text.Json.JsonSerializer.SerializeToElement(new { InstanceName = instance }));

    // --- Local-only exclusion: a frozen pre-cutover "engine-looking" row never resurfaces -------------
    [Fact]
    public async Task PageMergedAsync_ExcludesEngineSourcedActionsFromLocal_EvenWithNoMonitor()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Simulates a row frozen in the local table from BEFORE the Phase-C cutover (KgsmAuditConsumer no
        // longer writes these, but old rows already there don't vanish from the DB — just from the merge).
        await SeedLocalAsync(AuditAction.ServerStart, now, "evt_frozen1");
        await SeedLocalAsync(AuditAction.FileWrite, now.AddSeconds(1), "evt_apionly1");

        var fake = new FakeEventJournal(_ => EventHistoryPage.Unreadable); // unreadable — degrade, local-only
        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 50,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);

        Assert.True(page.EngineHistoryDegraded);
        Assert.Single(page.Data);
        Assert.Equal(AuditAction.FileWrite, page.Data[0].Action); // the frozen server.start row is excluded
    }

    // --- Monitor down: local-only rows + an honest degraded marker, never a 500 -----------------------
    [Fact]
    public async Task PageMergedAsync_MonitorDown_LocalOnly_DegradedMarkerTrue()
    {
        await SeedLocalAsync(AuditAction.FileWrite, DateTimeOffset.UtcNow, "evt_local1");
        var fake = new FakeEventJournal(_ => EventHistoryPage.Unreadable);

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 50,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);

        Assert.True(page.EngineHistoryDegraded);
        Assert.Single(page.Data);
        Assert.Equal("evt_local1", page.Data[0].Id);
    }

    // --- A reader that throws degrades honestly, never a 500 -----------------------------------------
    // The journal reader's contract is that it does not throw for a missing or unreadable journal, so
    // this covers the case where it breaks that contract anyway. The audit endpoint is how an operator
    // finds out what happened; failing it closed because one of its two sources misbehaved would hide
    // the local half too, at exactly the moment someone is looking.
    [Fact]
    public async Task PageMergedAsync_JournalReaderThrows_DegradesHonestly_NeverThrows()
    {
        await SeedLocalAsync(AuditAction.FileWrite, DateTimeOffset.UtcNow, "evt_local2");
        var fake = new FakeEventJournal(_ => throw new IOException("journal exploded"));

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 50,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);

        Assert.True(page.EngineHistoryDegraded);
        Assert.Single(page.Data);
        Assert.Equal("evt_local2", page.Data[0].Id);
    }

    // --- No engine on this host: absent history is degraded, not silently empty ----------------------
    // A host with no kgsm has no journal to read, so there is no IEventJournalHistory to inject. That is
    // a missing capability, and reporting it as "no engine events happened" would state silence as fact.
    [Fact]
    public async Task PageMergedAsync_NoEngineProvisioned_DegradesRatherThanClaimingNoEvents()
    {
        await SeedLocalAsync(AuditAction.FileWrite, DateTimeOffset.UtcNow, "evt_local3");

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, journal: null, HostId, cursor: null, limit: 50,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);

        Assert.True(page.EngineHistoryDegraded);
        Assert.Single(page.Data);
        Assert.Equal("evt_local3", page.Data[0].Id);
    }

    // --- Healthy monitor: local + monitor rows interleave in one ts-DESC feed -------------------------
    [Fact]
    public async Task PageMergedAsync_InterleavesBothSourcesByTsDescending()
    {
        DateTimeOffset t0 = new(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        await SeedLocalAsync(AuditAction.FileWrite, t0.AddSeconds(1), "evt_local1");   // 2nd oldest
        await SeedLocalAsync(AuditAction.FileWrite, t0.AddSeconds(3), "evt_local2");   // newest

        var fake = new FakeEventJournal(_ => FakeEventJournal.Page(
            EngineEvent("evt_mon1", t0.AddSeconds(2)),  // 2nd newest
            EngineEvent("evt_mon2", t0.AddSeconds(0)))); // oldest

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 10,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);

        Assert.False(page.EngineHistoryDegraded);
        Assert.Equal(4, page.Data.Count);
        Assert.Equal(["evt_local2", "evt_mon1", "evt_local1", "evt_mon2"], page.Data.Select(r => r.Id).ToArray());
        Assert.Null(page.NextCursor); // both sources exhausted (each returned < limit)
    }

    // --- serverId is pushed down to the monitor as instance= (every filter reaches both sources) ------
    [Fact]
    public async Task PageMergedAsync_PushesServerIdFilterToMonitorAsInstance()
    {
        string? capturedInstance = "not-set";
        var fake = new FakeEventJournal(q => { capturedInstance = q.Instance; return EventHistoryPage.Unreadable; });

        await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 10,
            severity: null, serverId: "factorio-test", actor: null, since: null, category: null, CancellationToken.None);

        Assert.Equal("factorio-test", capturedInstance);
    }

    // --- Cursor is stable and lossless across a genuine ts TIE spanning both sources -------------------
    [Fact]
    public async Task PageMergedAsync_TsTieAcrossSources_PagesStably_NoLossNoDuplicate()
    {
        DateTimeOffset tie = new(2026, 7, 18, 5, 0, 0, TimeSpan.Zero);
        await SeedLocalAsync(AuditAction.FileWrite, tie, "evt_local_tie");

        var fake = new FakeEventJournal(q =>
        {
            // Once the cursor has walked past the tied row the journal is "exhausted" (no cursor on
            // page 1; on page 2 it is set, so return empty — proves no duplicate and no loss).
            if (q.BeforeTsMs is not null) return FakeEventJournal.Page();
            return FakeEventJournal.Page(EngineEvent("evt_mon_tie", tie));
        });

        // limit=1 forces the tie to split across two pages.
        AuditPage page1 = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 1,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);
        Assert.Single(page1.Data);
        Assert.NotNull(page1.NextCursor);

        AuditPage page2 = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: page1.NextCursor, limit: 1,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);
        Assert.Single(page2.Data);

        var seen = new[] { page1.Data[0].Id, page2.Data[0].Id };
        Assert.Contains("evt_local_tie", seen);
        Assert.Contains("evt_mon_tie", seen);
        Assert.NotEqual(page1.Data[0].Id, page2.Data[0].Id); // no duplicate across the tie boundary
    }

    // --- category/severity filters narrow the shaped monitor records too (not just the EF side) -------
    [Fact]
    public async Task PageMergedAsync_CategoryFilter_APIsToMonitorShapedRecordsToo()
    {
        var fake = new FakeEventJournal(_ => FakeEventJournal.Page(
            EngineEvent("evt_started", DateTimeOffset.UtcNow, "instance_started"),                 // -> server.start
            EngineEvent("evt_crashed", DateTimeOffset.UtcNow.AddSeconds(1), "instance_crashed"))); // -> server.crash

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 10,
            severity: null, serverId: null, actor: null, since: null, category: "server", CancellationToken.None);

        Assert.Equal(2, page.Data.Count); // both are server.* — category="server" keeps both
        Assert.All(page.Data, r => Assert.StartsWith("server.", r.Action));
    }

    /// <summary>
    /// Walking the merged feed to exhaustion must terminate, visiting every row exactly once.
    /// </summary>
    /// <remarks>
    /// The regression: the cursor's id belongs to whichever source supplied the page's last row, so
    /// it is regularly a LOCAL id that names no journal event. A reader that treated an id it could
    /// not place as "no cursor" restarted from the newest page every time that happened, and the walk
    /// never ended — 10,845 rows over 457 distinct ones, on a live host. Passing the timestamp as
    /// well as the id is what bounds the page when the id is foreign. Interleaving the two sources
    /// tightly is what makes the local side supply the boundary often enough to catch it.
    /// </remarks>
    [Fact]
    public async Task PageMergedAsync_WalkingToExhaustion_Terminates_AndVisitsEachRowOnce()
    {
        DateTimeOffset t0 = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

        var engine = new List<EventHistoryEntry>();
        for (int i = 0; i < 20; i++)
        {
            await SeedLocalAsync(AuditAction.FileWrite, t0.AddMinutes(i * 2), $"evt_local{i:D2}");
            engine.Add(EngineEvent($"evt_2026-08-04_{i:D12}", t0.AddMinutes(i * 2 + 1)));
        }

        var fake = new FakeEventJournal(q =>
        {
            IEnumerable<EventHistoryEntry> rows = engine.OrderByDescending(e => e.Ts);
            if (q.BeforeTsMs is { } beforeMs)
            {
                rows = rows.Where(e =>
                {
                    long ms = e.Ts.ToUnixTimeMilliseconds();
                    return ms < beforeMs
                        || (ms == beforeMs && string.CompareOrdinal(e.Id, q.BeforeId) < 0);
                });
            }
            return FakeEventJournal.Page([.. rows.Take(q.Limit)]);
        });

        var seen = new List<string>();
        string? cursor = null;

        for (int guard = 0; guard < 100; guard++)
        {
            AuditPage page = await AuditQueries.PageMergedAsync(
                _db, fake, HostId, cursor, limit: 7,
                severity: null, serverId: null, actor: null, since: null, category: null,
                CancellationToken.None);

            seen.AddRange(page.Data.Select(r => r.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(40, seen.Count);
        Assert.Equal(40, seen.Distinct().Count());
    }

    /// <summary>
    /// Engine events that shape to nothing must not consume the page's budget.
    /// </summary>
    /// <remarks>
    /// The regression, measured on a live host: a journal fetch of 200 RAW events covering
    /// 2026-08-05→08-07 contained 49 silent types, so only 151 became rows. The merged page filled the
    /// rest from the local table, its last row was a local one at 2026-07-31, and the cursor advanced
    /// there — skipping every engine event between 07-31 and 08-05 permanently, while all of them sat
    /// in the journal. A short engine half is not a short page; it is a hole in the audit trail.
    /// </remarks>
    [Fact]
    public async Task PageMergedAsync_SilentEventsDoNotStarveTheEngineHalf_SoNoWindowIsSkipped()
    {
        DateTimeOffset t0 = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

        // Newest 8 engine events are all silent; the real ones sit behind them.
        var engine = new List<EventHistoryEntry>();
        for (int i = 0; i < 8; i++)
            engine.Add(EngineEvent($"evt_2026-08-04_{100 + i:D12}", t0.AddMinutes(100 + i), "instance_ready"));
        for (int i = 0; i < 8; i++)
            engine.Add(EngineEvent($"evt_2026-08-04_{i:D12}", t0.AddMinutes(i), "instance_started"));

        // Local rows older than every engine event, so they are what fills a starved page.
        for (int i = 0; i < 8; i++)
            await SeedLocalAsync(AuditAction.FileWrite, t0.AddMinutes(-10 - i), $"evt_local{i:D2}");

        var fake = new FakeEventJournal(q =>
        {
            IEnumerable<EventHistoryEntry> rows = engine.OrderByDescending(e => e.Ts);
            if (q.BeforeTsMs is { } beforeMs)
            {
                rows = rows.Where(e =>
                {
                    long ms = e.Ts.ToUnixTimeMilliseconds();
                    return ms < beforeMs || (ms == beforeMs && string.CompareOrdinal(e.Id, q.BeforeId) < 0);
                });
            }
            EventHistoryEntry[] taken = [.. rows.Take(q.Limit)];
            return taken.Length == q.Limit
                ? new EventHistoryPage(taken, taken[^1].Ts.ToUnixTimeMilliseconds(), taken[^1].Id, null, false, true)
                : FakeEventJournal.Page(taken);
        });

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 8,
            severity: null, serverId: null, actor: null, since: null, category: null,
            CancellationToken.None);

        // All 8 slots go to real engine events — the silent ones cost nothing — so no local row is
        // pulled up past engine history that has not been served yet.
        Assert.Equal(8, page.Data.Count);
        Assert.All(page.Data, r => Assert.Equal(AuditAction.ServerStart, r.Action));
    }
}

/// <summary>Switch-on-input fake (the FakeDiscordResolver pattern) — deterministic per call, so parallel
/// tests never share mutable state.</summary>
internal sealed class FakeEventJournal(Func<EventHistoryQuery, EventHistoryPage> respond)
    : IEventJournalHistory
{
    /// <summary>A readable journal returning exactly these events.</summary>
    public static EventHistoryPage Page(params EventHistoryEntry[] events) =>
        new(events, null, null, events.Length > 0 ? events[^1].Ts : null, false, true);

    public Task<EventHistoryPage> QueryAsync(EventHistoryQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(respond(query));
}
