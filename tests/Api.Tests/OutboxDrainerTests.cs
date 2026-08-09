using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Cluster;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Phase 3 of the cluster message bus (<c>docs/cluster-message-bus-plan.md §6/§7/§8</c>) — the outbox
/// drainer + <see cref="IClusterBus.EnqueueAsync"/> + the retention GC. Deliberately NOT a
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> test (unlike
/// <see cref="ClusterInboxTests"/>) — there is no HTTP endpoint under test here, just the drainer's own
/// send/backoff/TTL logic and the GC's prune, against a real temp-file SQLite <see cref="AppDbContext"/>
/// (the <see cref="LibraryHydrationWorkerTests"/>/<see cref="SessionCleanupTests"/> pattern) and a fake
/// <see cref="HttpMessageHandler"/> standing in for the peer, so delivery outcomes are fully
/// deterministic (no real network, no real peer).
/// </summary>
public sealed class OutboxDrainerTests
{
    private const string NodeId = "node-a";

    // --- 1. EnqueueAsync -----------------------------------------------------------------------------

    [Fact]
    public async Task EnqueueAsync_TwoTargets_WritesTwoPendingRows_WithCorrectIdsUrlsAndPayload()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);

        var targets = new[]
        {
            new ClusterTarget("node-b", "https://node-b:8097"),
            new ClusterTarget("node-c", "https://node-c:8097/"), // trailing slash, on purpose
        };
        await bus.EnqueueAsync("session.revoke", new { scope = "sid", sid = "abc123" }, targets, default);

        List<OutboxMessage> rows = await AllRowsAsync(scopeFactory);
        Assert.Equal(2, rows.Count);

        OutboxMessage toB = Assert.Single(rows, r => r.TargetNodeId == "node-b");
        OutboxMessage toC = Assert.Single(rows, r => r.TargetNodeId == "node-c");

        // Same messageId shared across the broadcast (one logical envelope, two delivery rows).
        Assert.Equal(toB.MessageId, toC.MessageId);
        Assert.Equal($"{toB.MessageId}:node-b", toB.Id);
        Assert.Equal($"{toC.MessageId}:node-c", toC.Id);
        Assert.Equal("https://node-b:8097", toB.TargetUrl);
        Assert.Equal("https://node-c:8097/", toC.TargetUrl);
        Assert.Equal("session.revoke", toB.Type);
        Assert.Equal("pending", toB.Status);
        Assert.Equal(0, toB.Attempts);
        Assert.Null(toB.DeliveredAt);

        using JsonDocument payload = JsonDocument.Parse(toB.Payload);
        Assert.Equal("sid", payload.RootElement.GetProperty("scope").GetString());
        Assert.Equal("abc123", payload.RootElement.GetProperty("sid").GetString());
    }

    [Fact]
    public async Task EnqueueAsync_NoTargets_WritesNoRows()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);

        await bus.EnqueueAsync("session.revoke", new { scope = "all" }, [], default);

        Assert.Empty(await AllRowsAsync(scopeFactory));
    }

    // --- 2. Drain — 200 → delivered ------------------------------------------------------------------

    [Fact]
    public async Task Drain_PeerReturns200_BothRowsDelivered_WithDeliveredAtSet_AndEnvelopeShapedCorrectly()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);
        var targets = new[]
        {
            new ClusterTarget("node-b", "https://node-b.test"),
            new ClusterTarget("node-c", "https://node-c.test"),
        };
        await bus.EnqueueAsync("session.revoke", new { scope = "sid", sid = "abc123" }, targets, default);

        var handler = new ScriptedHandler(HttpStatusCode.OK);
        OutboxDrainer drainer = NewDrainer(bus, handler);

        await drainer.RunDrainPassAsync(default);

        List<OutboxMessage> rows = await AllRowsAsync(scopeFactory);
        Assert.All(rows, r => Assert.Equal("delivered", r.Status));
        Assert.All(rows, r => Assert.NotNull(r.DeliveredAt));

        Assert.Equal(2, handler.Requests.Count);
        foreach (RecordedCall call in handler.Requests)
        {
            Assert.EndsWith("/api/v1/peers/inbox", call.Uri);
            using JsonDocument envelope = JsonDocument.Parse(call.Body);
            Assert.Equal(NodeId, envelope.RootElement.GetProperty("from").GetString());
            Assert.Equal("session.revoke", envelope.RootElement.GetProperty("type").GetString());
            Assert.True(envelope.RootElement.TryGetProperty("payload", out JsonElement payload));
            Assert.Equal("sid", payload.GetProperty("scope").GetString());
        }
    }

    // --- 3. Drain — 503 → stays pending, Attempts=1, NextAttemptAt in the future, LastError set -------

    [Fact]
    public async Task Drain_PeerReturns503_RowStaysPending_AttemptsIncremented_NextAttemptInFuture_LastErrorSet()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);
        await bus.EnqueueAsync(
            "session.revoke", new { scope = "sid", sid = "abc" },
            [new ClusterTarget("node-b", "https://node-b.test")], default);

        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable);
        OutboxDrainer drainer = NewDrainer(bus, handler);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        await drainer.RunDrainPassAsync(default);

        OutboxMessage row = Assert.Single(await AllRowsAsync(scopeFactory));
        Assert.Equal("pending", row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.True(row.NextAttemptAt > before);
        Assert.NotNull(row.LastError);
        Assert.Null(row.DeliveredAt);
    }

    // --- 4. Drain — 400 → dead ------------------------------------------------------------------------

    [Fact]
    public async Task Drain_PeerReturns400_RowDeadLettered()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);
        await bus.EnqueueAsync(
            "session.revoke", new { scope = "sid", sid = "abc" },
            [new ClusterTarget("node-b", "https://node-b.test")], default);

        var handler = new ScriptedHandler(HttpStatusCode.BadRequest);
        OutboxDrainer drainer = NewDrainer(bus, handler);

        await drainer.RunDrainPassAsync(default);

        OutboxMessage row = Assert.Single(await AllRowsAsync(scopeFactory));
        Assert.Equal("dead", row.Status);
        Assert.NotNull(row.LastError);
        Assert.Null(row.DeliveredAt);
    }

    // --- 5. A row past the retry TTL is dead-lettered WITHOUT calling the peer ------------------------

    [Fact]
    public async Task Drain_RowPastRetryTtl_DeadLettered_WithoutCallingThePeer()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SeedRowAsync(scopeFactory, new OutboxMessage
        {
            Id = "msg1:node-b", MessageId = "msg1", TargetNodeId = "node-b", TargetUrl = "https://node-b.test",
            Type = "session.revoke", Payload = """{"scope":"sid","sid":"abc"}""",
            Status = "pending", Attempts = 3, NextAttemptAt = now.AddMinutes(-1),
            CreatedAt = now.AddDays(-8), // > the default 7-day retry TTL
        });

        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);
        // A handler that would fail the test outright if the drainer ever calls it for this row.
        var handler = new ScriptedHandler(HttpStatusCode.OK);
        OutboxDrainer drainer = NewDrainer(bus, handler);

        await drainer.RunDrainPassAsync(default);

        OutboxMessage row = Assert.Single(await AllRowsAsync(scopeFactory));
        Assert.Equal("dead", row.Status);
        Assert.Contains("TTL", row.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests); // never sent — the TTL check runs before any HTTP call
    }

    // --- 6. GC PruneAsync ------------------------------------------------------------------------------

    [Fact]
    public async Task PruneAsync_DeletesOldDeliveredAndDeadRows_KeepsFreshOnes_AndPrunesOldInboxRows()
    {
        (IServiceScopeFactory scopeFactory, _) = NewDb();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cutoff = now.AddDays(-30);

        await SeedRowAsync(scopeFactory, Row("old-delivered:node-b", "delivered", now.AddDays(-31)));
        await SeedRowAsync(scopeFactory, Row("old-dead:node-b", "dead", now.AddDays(-40)));
        await SeedRowAsync(scopeFactory, Row("fresh-delivered:node-b", "delivered", now.AddDays(-1)));
        // A still-pending row older than the cutoff must survive prune regardless of age (only
        // delivered/dead are ever pruned — a pending row is still awaiting delivery).
        await SeedRowAsync(scopeFactory, Row("old-pending:node-b", "pending", now.AddDays(-90)));

        using (IServiceScope seedScope = scopeFactory.CreateScope())
        {
            AppDbContext db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ClusterInbox.Add(new InboxMessage
            {
                Id = "old-inbox", FromNodeId = "node-b", Type = "session.revoke",
                ReceivedAt = now.AddDays(-31), ProcessedAt = now.AddDays(-31),
            });
            db.ClusterInbox.Add(new InboxMessage
            {
                Id = "fresh-inbox", FromNodeId = "node-b", Type = "session.revoke",
                ReceivedAt = now.AddDays(-1), ProcessedAt = now.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var bus = new ClusterBus(scopeFactory, NullLogger<ClusterBus>.Instance);
        int deleted = await bus.PruneAsync(cutoff, now, default);

        Assert.Equal(3, deleted); // old-delivered + old-dead outbox, old-inbox — NOT old-pending

        List<OutboxMessage> remainingOutbox = await AllRowsAsync(scopeFactory);
        Assert.Equal(2, remainingOutbox.Count);
        Assert.Contains(remainingOutbox, r => r.Id == "fresh-delivered:node-b");
        Assert.Contains(remainingOutbox, r => r.Id == "old-pending:node-b"); // survives — still pending

        using IServiceScope verifyScope = scopeFactory.CreateScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<string> remainingInboxIds = await verifyDb.ClusterInbox.Select(i => i.Id).ToListAsync();
        Assert.Equal(["fresh-inbox"], remainingInboxIds);
    }

    private static OutboxMessage Row(string id, string status, DateTimeOffset createdAt)
    {
        int colon = id.IndexOf(':');
        return new OutboxMessage
        {
            Id = id,
            MessageId = id[..colon],
            TargetNodeId = id[(colon + 1)..],
            TargetUrl = "https://node-b.test",
            Type = "session.revoke",
            Payload = """{"scope":"sid","sid":"abc"}""",
            Status = status,
            Attempts = status == "pending" ? 5 : 0,
            NextAttemptAt = createdAt,
            CreatedAt = createdAt,
            DeliveredAt = status == "delivered" ? createdAt : null,
        };
    }

    // --- helpers --------------------------------------------------------------------------------------

    private static (IServiceScopeFactory ScopeFactory, string DbPath) NewDb()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"outboxtest-{Guid.NewGuid():N}.db");
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        return (provider.GetRequiredService<IServiceScopeFactory>(), dbPath);
    }

    private static async Task<List<OutboxMessage>> AllRowsAsync(IServiceScopeFactory scopeFactory)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ClusterOutbox.AsNoTracking().ToListAsync();
    }

    private static async Task SeedRowAsync(IServiceScopeFactory scopeFactory, OutboxMessage row)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ClusterOutbox.Add(row);
        await db.SaveChangesAsync();
    }

    private static ApiOptions TestOptions() => new()
    {
        HostId = NodeId, HostLabel = NodeId,
        MonitorSocketPath = "", WatchdogSocketPath = "", AssistantBaseUrl = "", AssistantRelaySecret = "",
        FirewallSocketPath = "", SchedulerSocketPath = "", BotSocketPath = "", KgsmPath = "/usr/bin/kgsm", KgsmJournalDir = "/var/lib/kgsm/events",
        BlueprintCacheTtlSeconds = 60,
        InstanceCacheTtlSeconds = 60,
        LogSources = [], JournalctlPath = "journalctl", SystemctlPath = "systemctl", LogReadTimeoutMs = 5000,
        RawgApiKey = "", RawgCacheDir = Path.GetTempPath(), PublicBaseUrl = "",
        SteamCdnBaseUrl = "https://steamcdn.test/apps",
        LibraryRefreshIntervalDays = 7, LibraryRefreshHour = 6,
        FilesMaxEntries = 200, FilesMaxEditBytes = 2 * 1024 * 1024, BlueprintMaxEditBytes = 256 * 1024,
        LeafOverridesDir = "/tmp/kgsm-api-test-overrides", LeafApplyCanaryMs = 15000,
        LeafDescriptorDir = "/tmp/kgsm-api-test-descriptors", LeafDropInDir = "/tmp/kgsm-api-test-dropins",
        DomainPollMs = 5000, MetricsPollMs = 1000, ServicesPollMs = 5000, UpdateCheckPollMs = 600000,


        AuthDisabled = true, SigningKey = "", DiscordClientId = "", DiscordClientSecret = "",
        DiscordRedirectUri = "", AuthFrontendUrl = "",
        SessionsCacheTtlMs = 5000, SessionsGcMs = 600000, SessionsRefreshAbsoluteDays = 30,

        // The cluster bus, ON (a non-blank secret) — this node's identity is NodeId ("node-a"), the
        // envelope `from` every test above asserts.
        ClusterSecret = "test-cluster-secret", ClusterSecretPrevious = "", NodeId = NodeId,
    };

    private static OutboxDrainer NewDrainer(ClusterBus bus, HttpMessageHandler handler)
    {
        var tokens = new ClusterTokenService(TestOptions(), NullLogger<ClusterTokenService>.Instance);
        return new OutboxDrainer(
            bus, tokens, TestOptions(), new FakeHttpClientFactory(handler), NullLogger<OutboxDrainer>.Instance);
    }

    /// <summary>A fake <see cref="IHttpClientFactory"/> that always hands back a fresh
    /// <see cref="HttpClient"/> wrapping the SAME <see cref="HttpMessageHandler"/> instance (never
    /// disposing it — the drainer creates one client per row processed) — no DI container, no real
    /// network, the same "new HttpClient(fakeHandler)" seam <c>DiscordSendTests</c> uses, just behind
    /// the interface the drainer actually depends on.</summary>
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Scripted peer response: returns a fixed status for every request and records each one
    /// (uri + body) so a test can assert what the drainer actually sent.</summary>
    private sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<RecordedCall> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new RecordedCall(request.RequestUri?.ToString() ?? "", body));
            return new HttpResponseMessage(status);
        }
    }

    private sealed record RecordedCall(string Uri, string Body);
}
