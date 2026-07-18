using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="AuditQueries.PageMergedAsync"/> — the event-history-plan.md Phase C merge reader: a
/// ts-DESC page over {local API-only rows} ∪ {kgsm-monitor's shaped engine rows}. Pure unit tests against
/// a real (file-backed) SQLite <see cref="AppDbContext"/> and a hand-rolled <see cref="IMonitorEventsClient"/>
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

    private static MonitorEventItem EngineEvent(string id, DateTimeOffset ts, string type = "instance_started", string instance = "mc") =>
        new(id, ts, type, instance, null, null, System.Text.Json.JsonSerializer.SerializeToElement(new { InstanceName = instance }));

    // --- Local-only exclusion: a frozen pre-cutover "engine-looking" row never resurfaces -------------
    [Fact]
    public async Task PageMergedAsync_ExcludesEngineSourcedActionsFromLocal_EvenWithNoMonitor()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Simulates a row frozen in the local table from BEFORE the Phase-C cutover (KgsmAuditConsumer no
        // longer writes these, but old rows already there don't vanish from the DB — just from the merge).
        await SeedLocalAsync(AuditAction.ServerStart, now, "evt_frozen1");
        await SeedLocalAsync(AuditAction.FileWrite, now.AddSeconds(1), "evt_apionly1");

        var fake = new FakeMonitorEventsClient(_ => null); // no monitor — degrade, local-only
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
        var fake = new FakeMonitorEventsClient(_ => null);

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 50,
            severity: null, serverId: null, actor: null, since: null, category: null, CancellationToken.None);

        Assert.True(page.EngineHistoryDegraded);
        Assert.Single(page.Data);
        Assert.Equal("evt_local1", page.Data[0].Id);
    }

    // --- Healthy monitor: local + monitor rows interleave in one ts-DESC feed -------------------------
    [Fact]
    public async Task PageMergedAsync_InterleavesBothSourcesByTsDescending()
    {
        DateTimeOffset t0 = new(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        await SeedLocalAsync(AuditAction.FileWrite, t0.AddSeconds(1), "evt_local1");   // 2nd oldest
        await SeedLocalAsync(AuditAction.FileWrite, t0.AddSeconds(3), "evt_local2");   // newest

        var fake = new FakeMonitorEventsClient(_ => new MonitorEventPage(2, null, null,
        [
            EngineEvent("evt_mon1", t0.AddSeconds(2)), // 2nd newest
            EngineEvent("evt_mon2", t0.AddSeconds(0)), // oldest
        ]));

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
        var fake = new FakeMonitorEventsClient(req => { capturedInstance = req.Instance; return null; });

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

        var fake = new FakeMonitorEventsClient(req =>
        {
            // Once the cursor has walked past the tied row, the monitor is "exhausted" (no before_ts yet
            // on page 1; on page 2 the cursor is set, so return empty — proves no duplicate/loss).
            if (req.BeforeTs is not null) return new MonitorEventPage(0, null, null, []);
            return new MonitorEventPage(1, null, null, [EngineEvent("evt_mon_tie", tie)]);
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
        var fake = new FakeMonitorEventsClient(_ => new MonitorEventPage(2, null, null,
        [
            EngineEvent("evt_started", DateTimeOffset.UtcNow, "instance_started"),   // -> server.start
            EngineEvent("evt_crashed", DateTimeOffset.UtcNow.AddSeconds(1), "instance_crashed"), // -> server.crash
        ]));

        AuditPage page = await AuditQueries.PageMergedAsync(
            _db, fake, HostId, cursor: null, limit: 10,
            severity: null, serverId: null, actor: null, since: null, category: "server", CancellationToken.None);

        Assert.Equal(2, page.Data.Count); // both are server.* — category="server" keeps both
        Assert.All(page.Data, r => Assert.StartsWith("server.", r.Action));
    }
}

/// <summary>Switch-on-input fake (the FakeDiscordResolver pattern) — deterministic per call, so parallel
/// tests never share mutable state.</summary>
internal sealed class FakeMonitorEventsClient(Func<FakeMonitorEventsClient.Request, MonitorEventPage?> respond)
    : IMonitorEventsClient
{
    public readonly record struct Request(
        string? Instance, string? Type, long? SinceMs, long? UntilMs, long? BeforeTs, string? BeforeId, int Limit);

    public Task<MonitorEventPage?> GetEventsAsync(
        string? instance, string? type, long? sinceMs, long? untilMs,
        long? beforeTs, string? beforeId, int limit, CancellationToken ct) =>
        Task.FromResult(respond(new Request(instance, type, sinceMs, untilMs, beforeTs, beforeId, limit)));
}
