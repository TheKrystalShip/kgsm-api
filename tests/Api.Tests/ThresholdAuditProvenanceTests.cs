using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Who a threshold audit row says established it. This API writes these rows but does not decide anything
/// in them — kgsm-monitor evaluates the rules against every sample it takes — so the trail has to name the
/// monitor rather than a bare <c>system</c>, the way kgsm-watchdog's autonomous rows already name
/// <c>watchdog</c>. Otherwise the log gains a second class of anonymous rows and nobody can ask which
/// component acted.
/// </summary>
public sealed class ThresholdAuditProvenanceTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    // --- the actor form -------------------------------------------------------------------------------

    [Fact]
    public void MonitorActor_RoundTripsThroughTheExistingParser()
    {
        // The ecosystem's autonomous-emitter form, and the reason it is spelled with a prefix at all: an
        // unprefixed actor is read downstream as a person on the local host.
        AuditActor actor = AuditMapping.ParseActor("system:monitor");

        Assert.Equal(ActorKind.System, actor.Kind);
        Assert.Equal("monitor", actor.Name);
        Assert.Equal(ActorProvider.System, actor.Provider);
    }

    [Fact]
    public void MonitorAndWatchdog_AreTheSameFormWithDifferentNames()
    {
        AuditActor monitor = AuditMapping.ParseActor("system:monitor");
        AuditActor watchdog = AuditMapping.ParseActor("system:watchdog");

        Assert.Equal(monitor.Kind, watchdog.Kind);
        Assert.Equal(monitor.Provider, watchdog.Provider);
        Assert.NotEqual(monitor.Name, watchdog.Name);
    }

    [Fact]
    public void ABareSystemActor_IsNotTheMonitor()
    {
        // ParseActor's defensive fallback for an empty actor. Nothing in the threshold path produces one —
        // the monitor's identity is known at the point of writing — so a threshold row that came out like
        // this would be a bug, not a degraded case.
        AuditActor bare = AuditMapping.ParseActor(null);

        Assert.Equal(ActorKind.System, bare.Kind);
        Assert.Equal("system", bare.Name);
        Assert.NotEqual("monitor", bare.Name);
    }

    // --- the rows are distinguishable in the feed -----------------------------------------------------

    [Fact]
    public async Task ThresholdRows_AreFilterableByTheMonitorActor()
    {
        AuditService audit = factory.Services.GetRequiredService<AuditService>();
        string marker = "thr-" + Guid.NewGuid().ToString("N")[..8];

        await audit.AppendAsync(BreachWrite(marker));
        await audit.AppendAsync(ClearWrite(marker));

        HttpClient c = Client(KgsmTier.Operator);

        var byMonitor = await Actions(c, "/api/v1/audit?actor=monitor&limit=200", marker);
        Assert.Contains(AuditAction.HostThresholdBreach, byMonitor);
        Assert.Contains(AuditAction.HostThresholdClear, byMonitor);

        // The negatives are what prove the rows are ATTRIBUTED rather than merely labelled: an operator
        // asking what the watchdog did, or filtering the anonymous autonomous rows, must not be handed
        // these. Naming the other producer explicitly is the point — `watchdog` is a real actor in this
        // log, not a value nothing uses.
        Assert.Empty(await Actions(c, "/api/v1/audit?actor=watchdog&limit=200", marker));
        Assert.Empty(await Actions(c, "/api/v1/audit?actor=system&limit=200", marker));
    }

    [Fact]
    public async Task ThresholdRows_AreLocal_NotExpectedFromTheEngineJournal()
    {
        // host.threshold.* are direct writes, like auth.* — the monitor emits no kgsm event, so there is no
        // echo and nothing to double-write. That means they must NOT join the engine-owned action list, or
        // GET /audit would read them from the journal (where they never appear) and this API's own rows
        // would be invisible. This asserts the outcome of that, not the list.
        AuditService audit = factory.Services.GetRequiredService<AuditService>();
        string marker = "thr-" + Guid.NewGuid().ToString("N")[..8];
        await audit.AppendAsync(BreachWrite(marker));

        HttpClient c = Client(KgsmTier.Operator);
        Assert.Single(await Actions(c, "/api/v1/audit?limit=200", marker));
    }

    [Fact]
    public async Task ABreachRow_CarriesItsEpisodeIdAndOrigin()
    {
        AuditService audit = factory.Services.GetRequiredService<AuditService>();
        string marker = "thr-" + Guid.NewGuid().ToString("N")[..8];
        await audit.AppendAsync(BreachWrite(marker));

        HttpClient c = Client(KgsmTier.Operator);
        JsonElement page = await Json(await c.GetAsync("/api/v1/audit?actor=monitor&limit=200"));
        JsonElement row = page.GetProperty("data").EnumerateArray()
            .First(r => r.GetProperty("summary").GetString()!.Contains(marker));

        // Origin is the surface that drove it, and none did. Producer identity lives in the actor, which is
        // the field for it — the closed origin vocabulary has no per-component value.
        Assert.Equal(AuditOrigin.System, row.GetProperty("origin").GetString());
        Assert.Equal("monitor", row.GetProperty("actor").GetProperty("name").GetString());

        // The episode id is what makes the row deduplicable against a re-read of the monitor's record.
        Assert.Equal(marker + ":1000", row.GetProperty("meta").GetProperty("episodeId").GetString());
    }

    // --- helpers --------------------------------------------------------------------------------------

    private static AuditWrite BreachWrite(string marker) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
        Actor: AuditMapping.ParseActor("system:monitor"),
        Action: AuditAction.HostThresholdBreach, Severity: AuditSeverity.Warn,
        Target: new AuditTarget(AuditTargetKind.Host, "test-host", "test-host"),
        ServerId: null, HostId: "test-host",
        Summary: $"{marker} temperature crossed 85°C",
        Meta: new Dictionary<string, string> { ["episodeId"] = marker + ":1000", ["ruleKey"] = "host-temp" });

    private static AuditWrite ClearWrite(string marker) => new(
        Ts: DateTimeOffset.UtcNow, Origin: AuditOrigin.System,
        Actor: AuditMapping.ParseActor("system:monitor"),
        Action: AuditAction.HostThresholdClear, Severity: AuditSeverity.Info,
        Target: new AuditTarget(AuditTargetKind.Host, "test-host", "test-host"),
        ServerId: null, HostId: "test-host",
        Summary: $"{marker} temperature back to normal after 2m",
        Meta: new Dictionary<string, string> { ["episodeId"] = marker + ":1000" });

    private async Task<List<string>> Actions(HttpClient c, string url, string marker)
    {
        JsonElement page = await Json(await c.GetAsync(url));
        return page.GetProperty("data").EnumerateArray()
            .Where(r => r.GetProperty("summary").GetString()!.Contains(marker))
            .Select(r => r.GetProperty("action").GetString()!)
            .ToList();
    }

    private HttpClient Client(KgsmTier tier)
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(tier));
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
}
