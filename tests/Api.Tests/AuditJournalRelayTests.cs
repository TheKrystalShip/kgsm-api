using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The engine-event relay: a line appended to kgsm's event journal reaches a subscribed client on the
/// <c>audit</c> SSE topic, with the engine's own provenance intact.
/// <para>
/// This is the leg no other suite covers. <c>AuditTests.AuditTopic_DeliversAppend</c> starts at
/// <see cref="Services.Audit.AuditService"/> and proves append→SSE; everything upstream of it — the
/// journal tail (<c>Api__KgsmJournalDir</c>), kgsm-lib's reader, <see cref="Services.Audit.KgsmAuditConsumer"/>'s
/// typed handler, the <see cref="Services.Audit.AuditMapping"/> shaping — is exercised only by driving a
/// real journal file, which is what this does. The journal here is a temp directory, so no host state
/// is involved: the engine's transport is a shared host-wide file, and a test that wrote to the real
/// one would land permanently in the operator's audit log.
/// </para>
/// <para>
/// The second test locks the other half of the contract: the relay publishes live but never PERSISTS an
/// engine-sourced row locally (kgsm-monitor owns that history and <c>GET /audit</c> merges it at read
/// time). A regression there is invisible in the UI and shows up only as duplicated rows once a monitor
/// is present.
/// </para>
/// </summary>
public sealed class AuditJournalRelayTests : IClassFixture<AuditJournalRelayTests.JournalFactory>
{
    private readonly JournalFactory _factory;

    public AuditJournalRelayTests(JournalFactory factory) => _factory = factory;

    [Fact]
    public async Task JournalLine_IsRelayedToTheAuditTopic_CarryingEngineProvenance()
    {
        // Hold the client for the whole read: the TestHost aborts the response body the moment its
        // HttpClient is collected, which surfaces as a mid-stream IOException rather than a clean fail.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            client, "/api/v1/stream?topics=audit", _factory.AccessToken(KgsmTier.Viewer));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        // Append across the read window rather than once: the journal reader polls, and there is no ack
        // to synchronize on, so re-appending until a frame lands avoids a sleep-tuned race. Every line
        // names the same instance, so any one of them satisfies the assertions.
        string instance = $"relay-{Guid.NewGuid():N}";
        JsonElement? frame = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && frame is null)
        {
            _factory.AppendEvent("instance_started", instance,
                actor: "discord:relaytest", origin: AuditOrigin.Ui);
            frame = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "audit.append"
                     && f.GetProperty("data").TryGetProperty("serverId", out JsonElement s)
                     && s.GetString() == instance,
                TimeSpan.FromMilliseconds(750));
        }

        Assert.NotNull(frame);
        JsonElement data = frame!.Value.GetProperty("data");
        Assert.Equal("audit", frame.Value.GetProperty("topic").GetString());
        Assert.Equal(AuditAction.ServerStart, data.GetProperty("action").GetString());
        Assert.Equal(instance, data.GetProperty("serverId").GetString());

        // Provenance rides off the journal envelope — the API never invents either axis.
        Assert.Equal(AuditOrigin.Ui, data.GetProperty("origin").GetString());
        Assert.Equal("relaytest", data.GetProperty("actor").GetProperty("name").GetString());
        Assert.Equal("discord", data.GetProperty("actor").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task JournalLine_IsPublishedLive_ButNeverPersistedAsALocalRow()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            client, "/api/v1/stream?topics=audit", _factory.AccessToken(KgsmTier.Viewer));
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        string instance = $"nopersist-{Guid.NewGuid():N}";
        JsonElement? frame = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && frame is null)
        {
            _factory.AppendEvent("instance_stopped", instance, actor: "system:watchdog", origin: AuditOrigin.System);
            frame = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "audit.append"
                     && f.GetProperty("data").TryGetProperty("serverId", out JsonElement s)
                     && s.GetString() == instance,
                TimeSpan.FromMilliseconds(750));
        }

        Assert.NotNull(frame);   // it WAS delivered live…

        // …and this API wrote no row of its own for it. kgsm owns server.*, so a local write would be a
        // second copy of one fact — undedupable, since the two would differ only by which component
        // recorded them. The local table is checked directly: GET /audit cannot show the difference,
        // because a journal-sourced row and a locally-written one arrive there looking identical.
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(db.Audit.Where(a => a.ServerId == instance));
        }

        // It does reach the merged read — from the journal, which is the record. This is what makes
        // engine history independent of any leaf being installed.
        using HttpClient reader = _factory.CreateClient();
        reader.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.AccessToken(KgsmTier.Viewer));
        HttpResponseMessage page = await reader.GetAsync($"/api/v1/audit?serverId={instance}&limit=200");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        JsonElement row = Assert.Single(doc.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(AuditAction.ServerStop, row.GetProperty("action").GetString());

        // The live push and the stored read agree on the id, so a client reconciling the two sees one
        // fact — both derive it from the same journal position.
        Assert.Equal(frame!.Value.GetProperty("data").GetProperty("id").GetString(),
                     row.GetProperty("id").GetString());
        Assert.False(doc.RootElement.GetProperty("engineHistoryDegraded").GetBoolean());
    }

    /// <summary>
    /// The live half of <c>server.ready</c>. Both halves of the feed have to agree about it: the merged
    /// read shapes it out of the journal, and this is the handler that pushes it as it happens — a row a
    /// client only ever saw on refresh would look like the panel had missed it.
    /// </summary>
    [Fact]
    public async Task ReadyIsPushedLiveAndNotOnlyOnRefresh()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            client, "/api/v1/stream?topics=audit", _factory.AccessToken(KgsmTier.Operator));
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        string instance = $"ready-{Guid.NewGuid():N}";
        JsonElement? frame = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && frame is null)
        {
            _factory.AppendEvent("instance_ready", instance, actor: "system:watchdog", origin: AuditOrigin.System);
            frame = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "audit.append"
                     && f.GetProperty("data").TryGetProperty("serverId", out JsonElement s)
                     && s.GetString() == instance,
                TimeSpan.FromMilliseconds(750));
        }

        Assert.NotNull(frame);
        Assert.Equal(AuditAction.ServerReady, frame!.Value.GetProperty("data").GetProperty("action").GetString());
    }

    /// <summary>
    /// <b>The decision this proves, end to end from the journal:</b> a player's connection address is on
    /// the Control Panel for an operator and not below one. Both readers see the join — the row is never
    /// withheld — and the name the game itself shows is on both, because that is what a name is for.
    /// </summary>
    [Fact]
    public async Task APlayersAddressReachesAnOperatorAndNotAViewer()
    {
        string instance = $"addr-{Guid.NewGuid():N}";
        _factory.AppendEvent(
            "instance_player_joined",
            new { InstanceName = instance, PlayerName = "bob", PlayerAddr = "95.49.44.91" },
            actor: "system:watchdog", origin: AuditOrigin.System);

        JsonElement operatorRow = await SingleRow(KgsmTier.Operator, instance);
        JsonElement viewerRow = await SingleRow(KgsmTier.Viewer, instance);

        Assert.Equal("95.49.44.91", operatorRow.GetProperty("meta").GetProperty("playerAddr").GetString());
        Assert.Equal("bob", operatorRow.GetProperty("meta").GetProperty("playerName").GetString());

        Assert.False(viewerRow.GetProperty("meta").TryGetProperty("playerAddr", out _));
        Assert.Equal("bob", viewerRow.GetProperty("meta").GetProperty("playerName").GetString());

        // Same row, same id, same history — only the value inside it differs.
        Assert.Equal(operatorRow.GetProperty("id").GetString(), viewerRow.GetProperty("id").GetString());
        Assert.Equal(AuditAction.PlayerJoin, viewerRow.GetProperty("action").GetString());
    }

    /// <summary>
    /// The console command a viewer must not read is in the row's own sentence as well as its meta, and
    /// the page has to withhold both — a summary is the part a reader actually looks at.
    /// </summary>
    [Fact]
    public async Task AConsoleCommandIsWithheldFromTheSummaryAViewerReads()
    {
        string instance = $"cmd-{Guid.NewGuid():N}";
        _factory.AppendEvent(
            "instance_input_sent",
            new { InstanceName = instance, Command = "op somebody" },
            actor: "discord:haru", origin: AuditOrigin.Ui);

        JsonElement operatorRow = await SingleRow(KgsmTier.Operator, instance);
        JsonElement viewerRow = await SingleRow(KgsmTier.Viewer, instance);

        Assert.Contains("op somebody", operatorRow.GetProperty("summary").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("op somebody", viewerRow.GetProperty("summary").GetString()!, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, viewerRow.GetProperty("meta").ValueKind);

        // Still there, still attributable: withholding what was typed is not hiding that it was.
        Assert.Equal(AuditAction.ConsoleInput, viewerRow.GetProperty("action").GetString());
        Assert.Equal("haru", viewerRow.GetProperty("actor").GetProperty("name").GetString());
    }

    /// <summary>The one row for <paramref name="instance"/> on the merged page, read at a given tier.</summary>
    private async Task<JsonElement> SingleRow(KgsmTier tier, string instance)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.AccessToken(tier));

        HttpResponseMessage page = await client.GetAsync($"/api/v1/audit?serverId={instance}&limit=200");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        return Assert.Single(doc.RootElement.GetProperty("data").EnumerateArray()).Clone();
    }

    /// <summary>
    /// <see cref="AuthTestFactory"/> with the engine "provisioned" against a temp journal directory, so
    /// kgsm-lib's journal reader starts and tails a file this test owns. <c>KgsmPath</c> only has to be
    /// non-empty for the engine to count as provisioned; nothing here execs it (every kgsm call degrades,
    /// which is fine — these tests assert the event path alone).
    /// </summary>
    public sealed class JournalFactory : AuthTestFactory, IDisposable
    {
        private static readonly UTF8Encoding BomlessUtf8 = new(encoderShouldEmitUTF8Identifier: false);

        public string JournalDir { get; } =
            Path.Combine(Path.GetTempPath(), $"kgsm-journal-relay-{Guid.NewGuid():N}");

        /// <summary>Today's segment — the one file kgsm writes to and the reader tails.</summary>
        private string SegmentPath => Path.Combine(JournalDir, $"{DateTime.UtcNow:yyyy-MM-dd}.ndjson");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Directory.CreateDirectory(JournalDir);
            // Create today's segment EMPTY, before the host starts. The reader attaches at the tail of
            // what exists when it starts; letting the first append create the file makes the test a race
            // between that append and the reader's attach, which it loses often enough to be flaky.
            File.AppendAllText(SegmentPath, "", BomlessUtf8);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:KgsmPath"] = "/bin/true",
                    ["Api:KgsmJournalDir"] = JournalDir,
                });
            });
        }

        /// <summary>
        /// Append one event to today's segment, in the exact NDJSON envelope kgsm writes
        /// (<c>EventType</c>/<c>Data</c>/<c>Timestamp</c>/<c>Actor</c>/<c>Origin</c>/<c>Hostname</c>).
        /// </summary>
        public void AppendEvent(string eventType, string instance, string? actor, string? origin) =>
            AppendEvent(eventType, (object)new { InstanceName = instance }, actor, origin);

        /// <summary>
        /// The same, with the event's own payload — for the events whose <em>fields</em> are the point.
        /// </summary>
        public void AppendEvent(string eventType, object data, string? actor, string? origin)
        {
            string line = JsonSerializer.Serialize(new
            {
                EventType = eventType,
                Data = data,
                Timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Actor = actor,
                Origin = origin,
                Hostname = "test-host",
                KGSMVersion = "0.0.0-test",
            });

            // NO BOM. Encoding.UTF8 emits one when it creates the file, and the reader would hand
            // kgsm-lib a first line starting 0xEF — an unparseable event, silently dropped.
            File.AppendAllText(SegmentPath, line + "\n", BomlessUtf8);
        }

        public new void Dispose()
        {
            try { Directory.Delete(JournalDir, recursive: true); } catch (IOException) { /* best effort */ }
            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
