using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Models;

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
            _factory.AppendEvent("server.started", instance,
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
        Assert.Equal("server.started", data.GetProperty("action").GetString());
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
            _factory.AppendEvent("server.stopped", instance, actor: "system:watchdog", origin: AuditOrigin.System);
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
        Assert.Equal("server.stopped", row.GetProperty("action").GetString());

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
            _factory.AppendEvent("server.ready", instance, actor: "system:watchdog", origin: AuditOrigin.System);
            frame = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "audit.append"
                     && f.GetProperty("data").TryGetProperty("serverId", out JsonElement s)
                     && s.GetString() == instance,
                TimeSpan.FromMilliseconds(750));
        }

        Assert.NotNull(frame);
        Assert.Equal("server.ready", frame!.Value.GetProperty("data").GetProperty("action").GetString());
    }

    /// <summary>
    /// A restart follows the process through every state it is actually in: down while the old run is
    /// being drained, booting once the new one is spawned, up only when the watchdog says it is ready.
    /// Each of the three comes from an engine event — this API infers none of them, which is the point:
    /// the engine is the only thing that knows, and until it said so the instance read as running for
    /// the whole shutdown and as up for the whole boot.
    /// </summary>
    [Fact]
    public async Task ARestartFollowsTheProcessDownThenThroughItsBoot()
    {
        InstanceCache cache = _factory.Services.GetRequiredService<InstanceCache>();

        // Re-append until the tail picks it up — the reader polls and acks nothing (same shape as the
        // relay tests above), so the loop is what keeps this off a tuned sleep.
        string instance = $"restart-{Guid.NewGuid():N}";

        // The bracket opens the run. Appended once and not waited on — it claims the in-flight job slot
        // and says nothing about run-state, which is exactly the gap the events below close.
        _factory.AppendEvent("server.restart.started", instance, actor: "discord:haru", origin: AuditOrigin.Ui);

        // The stop half landed: the process does not exist, and the API says so rather than carrying the
        // state from before the restart.
        await Feed("server.restart.stopped", instance, until: () => Down(cache, instance));
        Assert.False(cache.IsStarting(instance));

        // The start half: spawned, booting — not yet a server anyone can join.
        await Feed("server.restarted", instance, until: () => cache.IsStarting(instance));
        Assert.True(cache.Statuses[instance].Value!.Status);

        // Ready closes it, exactly as it closes a plain start's window.
        await Feed("server.ready", instance, until: () => !cache.IsStarting(instance));
        Assert.True(cache.Statuses[instance].Value!.Status);
    }

    /// <summary>
    /// An engine-driven update that kgsm reports as failed settles the observed job as FAILED. It used
    /// to settle as succeeded — the bracket's finish is emitted on every outcome and this API has no
    /// exit code of its own for a run it did not issue, so a refused update reported itself to every
    /// surface as a completed one. The engine now states the outcome and this is where it is believed.
    /// </summary>
    [Fact]
    public async Task AnUpdateTheEngineReportsAsFailedSettlesTheJobAsFailed()
    {
        JobRegistry jobs = _factory.Services.GetRequiredService<JobRegistry>();

        string instance = $"updfail-{Guid.NewGuid():N}";
        await Feed("server.update.started", instance, until: () => jobs.InFlightFor(instance) is not null);

        Job running = jobs.InFlightFor(instance)!;
        Assert.Equal(CommandVerb.Update, running.Verb);

        await Feed("server.update.failed", instance,
            until: () => jobs.Get(running.Id)?.State == JobState.Failed);

        Job settled = jobs.Get(running.Id)!;
        Assert.Equal(JobState.Failed, settled.State);
        Assert.NotNull(settled.Error);
        Assert.Null(jobs.InFlightFor(instance));   // the slot is released either way
    }

    /// <summary>
    /// An engine-driven uninstall claims the in-flight slot for the whole of its run. Every other long
    /// verb's bracket did; this one was not registered, so a removal driven from anywhere but this API
    /// showed nothing at all on a surface while a server was being destroyed.
    /// </summary>
    [Fact]
    public async Task AnEngineDrivenUninstallIsVisibleWhileItRuns()
    {
        JobRegistry jobs = _factory.Services.GetRequiredService<JobRegistry>();

        string instance = $"uninst-{Guid.NewGuid():N}";
        await Feed("server.uninstall.started", instance, until: () => jobs.InFlightFor(instance) is not null);
        Assert.Equal(CommandVerb.Uninstall, jobs.InFlightFor(instance)!.Verb);

        await Feed("server.uninstall.finished", instance, until: () => jobs.InFlightFor(instance) is null);
    }

    /// <summary>The instance is known and observed down — measured, never merely unread.</summary>
    private static bool Down(InstanceCache cache, string instance) =>
        cache.Statuses.TryGetValue(instance, out Reading<InstanceRuntimeStatus>? r)
        && r.Value?.Status == false;

    /// <summary>
    /// Append <paramref name="type"/> for <paramref name="instance"/> until <paramref name="until"/>
    /// holds. Re-appending is deliberate: the journal reader polls and acknowledges nothing, so the
    /// alternative is a sleep tuned to its cadence. Every event here is idempotent in the cache.
    /// </summary>
    private async Task Feed(string type, string instance, Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !until())
        {
            _factory.AppendEvent(type, instance, actor: "discord:haru", origin: AuditOrigin.Ui);
            await Task.Delay(250);
        }

        Assert.True(until(), $"the cache never reflected {type} for {instance}");
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
            "player.joined",
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
        Assert.Equal("player.joined", viewerRow.GetProperty("action").GetString());
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
            "console.input.sent",
            new { InstanceName = instance, Command = "op somebody" },
            actor: "discord:haru", origin: AuditOrigin.Ui);

        JsonElement operatorRow = await SingleRow(KgsmTier.Operator, instance);
        JsonElement viewerRow = await SingleRow(KgsmTier.Viewer, instance);

        Assert.Contains("op somebody", operatorRow.GetProperty("summary").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("op somebody", viewerRow.GetProperty("summary").GetString()!, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, viewerRow.GetProperty("meta").ValueKind);

        // Still there, still attributable: withholding what was typed is not hiding that it was.
        Assert.Equal("console.input.sent", viewerRow.GetProperty("action").GetString());
        Assert.Equal("haru", viewerRow.GetProperty("actor").GetProperty("name").GetString());
    }

    /// <summary>
    /// A command outcome reaches the feed exactly ONCE.
    /// </summary>
    /// <remarks>
    /// The property the write site depends on: this API tails its own journal, and that tail is what
    /// shapes and announces the row. Announcing from the write site as well would send every one of
    /// these twice, and a duplicate is invisible to a reader — both frames carry the same id, the same
    /// sentence and the same timestamp, so the feed simply shows one fact as two.
    /// </remarks>
    [Fact]
    public async Task ACommandOutcomeIsAnnouncedOnceAndReadsBackTheSame()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            client, "/api/v1/stream?topics=audit", _factory.AccessToken(KgsmTier.Operator));
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        // Appended ONCE, deliberately: the count is the assertion, so the usual re-append-until-seen
        // loop would make a duplicate indistinguishable from a second append. The segment file exists
        // before the host starts, so the tail is attached to it and a single line is enough.
        string instance = $"cmdfail-{Guid.NewGuid():N}";
        _factory.AppendEvent(
            "command.failed",
            new { InstanceName = instance, Verb = "start", JobId = "job_once", ExitCode = 1, Error = "kgsm said no" },
            actor: "discord:haru", origin: AuditOrigin.Ui);

        bool Mine(JsonElement f) =>
            f.GetProperty("type").GetString() == "audit.append"
            && f.GetProperty("data").TryGetProperty("serverId", out JsonElement s)
            && s.GetString() == instance;

        JsonElement? first = await frames.WaitForFrame(Mine, TimeSpan.FromSeconds(20));
        Assert.NotNull(first);
        Assert.Equal("command.failed", first!.Value.GetProperty("data").GetProperty("action").GetString());
        Assert.Equal("kgsm said no",
            first.Value.GetProperty("data").GetProperty("meta").GetProperty("error").GetString());

        // Nothing announces it a second time.
        Assert.Null(await frames.WaitForFrame(Mine, TimeSpan.FromSeconds(2)));

        // And the merged read shows the same single fact under the same id — one journal position,
        // one row, whether it arrived live or on a refresh.
        JsonElement row = await SingleRow(KgsmTier.Operator, instance);
        Assert.Equal(first.Value.GetProperty("data").GetProperty("id").GetString(),
                     row.GetProperty("id").GetString());
        Assert.Equal("command.failed", row.GetProperty("action").GetString());
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
