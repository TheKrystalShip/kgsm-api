using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// M5 audit log, in-process against the real pipeline (the engine is unprovisioned, so the
/// event-sourced path is exercised by seeding through <see cref="AuditService"/> directly; the live
/// kgsm-socket path is the trusted-host validation, like M3's happy path). Covers the keyset query
/// (order, pagination, filters), the viewer gate, the API-internal auth.login write end-to-end, and
/// the <c>audit</c> SSE append. Each test scopes its rows by a unique serverId/actor so the shared
/// per-class DB never cross-contaminates assertions.
/// </summary>
public sealed class AuditTests(AuthTestFactory factory) : IClassFixture<AuthTestFactory>
{
    private AuditService Audit => factory.Services.GetRequiredService<AuditService>();

    private HttpClient Viewer()
    {
        HttpClient c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.AccessToken(AuthTier.Viewer));
        return c;
    }

    private static AuditWrite ServerWrite(string action, string serverId, string actorName = "haru",
        string severity = AuditSeverity.Info) =>
        new(DateTimeOffset.UtcNow, AuditOrigin.Ui,
            new AuditActor(ActorKind.User, actorName, ActorProvider.Discord),
            action, severity, new AuditTarget(AuditTargetKind.Server, serverId, serverId),
            serverId, AuthTestFactory.HostId, $"{action} {serverId}", null);

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // --- Auth gate ---------------------------------------------------------------------------------
    [Fact]
    public async Task GetAudit_NoToken_401()
    {
        HttpResponseMessage r = await factory.CreateClient().GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetAudit_Viewer_200_PageShape()
    {
        HttpResponseMessage r = await Viewer().GetAsync("/api/v1/audit?limit=1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        JsonElement body = await Json(r);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
        Assert.True(body.TryGetProperty("nextCursor", out _)); // present (string or null)
    }

    // --- Newest-first + record shape ---------------------------------------------------------------
    [Fact]
    public async Task GetAudit_NewestFirst_HonestShape()
    {
        string sid = $"order-{Guid.NewGuid():N}";
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStart, sid));
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStop, sid));

        JsonElement body = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}"));
        JsonElement[] rows = body.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(AuditAction.ServerStop, rows[0].GetProperty("action").GetString());  // newest first
        Assert.Equal(AuditAction.ServerStart, rows[1].GetProperty("action").GetString());

        JsonElement newest = rows[0];
        Assert.StartsWith("evt_", newest.GetProperty("id").GetString());
        Assert.Equal("ui", newest.GetProperty("origin").GetString());
        Assert.Equal("user", newest.GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal("haru", newest.GetProperty("actor").GetProperty("name").GetString());
        Assert.Equal("discord", newest.GetProperty("actor").GetProperty("provider").GetString());
        Assert.Equal(sid, newest.GetProperty("serverId").GetString());
        Assert.Equal("server", newest.GetProperty("target").GetProperty("kind").GetString());
    }

    // --- Keyset pagination -------------------------------------------------------------------------
    [Fact]
    public async Task GetAudit_KeysetPagination_WalksToEnd()
    {
        string sid = $"page-{Guid.NewGuid():N}";
        for (int i = 0; i < 3; i++)
            await Audit.AppendAsync(ServerWrite(AuditAction.ServerRestart, sid));

        // Page 1: limit 2 of 3 -> full page + a cursor.
        JsonElement p1 = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&limit=2"));
        Assert.Equal(2, p1.GetProperty("data").GetArrayLength());
        string cursor = p1.GetProperty("nextCursor").GetString()!;
        Assert.False(string.IsNullOrEmpty(cursor));

        // Page 2: the remaining 1 -> short page, no further cursor.
        JsonElement p2 = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&limit=2&cursor={cursor}"));
        Assert.Equal(1, p2.GetProperty("data").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, p2.GetProperty("nextCursor").ValueKind);
    }

    // --- Filters (severity / actor) map 1:1 to indexed columns -------------------------------------
    [Fact]
    public async Task GetAudit_Filters_ScopeRows()
    {
        string sid = $"filter-{Guid.NewGuid():N}";
        string actor = $"actor-{Guid.NewGuid():N}";
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStart, sid, actor, AuditSeverity.Info));
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerUninstall, sid, actor, AuditSeverity.Warn));

        JsonElement warn = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&severity=warn"));
        Assert.Equal(1, warn.GetProperty("data").GetArrayLength());
        Assert.Equal(AuditAction.ServerUninstall, warn.GetProperty("data")[0].GetProperty("action").GetString());

        JsonElement byActor = await Json(await Viewer().GetAsync($"/api/v1/audit?actor={actor}"));
        Assert.Equal(2, byActor.GetProperty("data").GetArrayLength()); // both rows share the unique actor
    }

    // --- severity accepts a comma set (the UI's "attention" = warn,danger) -------------------------
    [Fact]
    public async Task GetAudit_MultiSeverity_OrsTheSet()
    {
        string sid = $"sev-{Guid.NewGuid():N}";
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStart, sid, severity: AuditSeverity.Info));
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStop, sid, severity: AuditSeverity.Warn));
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerCrash, sid, severity: AuditSeverity.Danger));

        // "attention" pushes down as warn,danger → the two, never the info row.
        JsonElement att = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&severity=warn,danger"));
        string?[] actions = att.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Contains(AuditAction.ServerStop, actions);
        Assert.Contains(AuditAction.ServerCrash, actions);
        Assert.DoesNotContain(AuditAction.ServerStart, actions); // info excluded

        // a stray/whitespace entry in the set is dropped, not matched as a blank severity.
        JsonElement spaced = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&severity=warn,%20,danger"));
        Assert.Equal(2, spaced.GetProperty("data").GetArrayLength());
    }

    // --- since: an ISO lower bound (the time-range tabs) -------------------------------------------
    [Fact]
    public async Task GetAudit_Since_LowerBounds()
    {
        string sid = $"since-{Guid.NewGuid():N}";
        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-10);
        // a backdated row + a fresh one (ServerWrite stamps UtcNow)
        await Audit.AppendAsync(new AuditWrite(old, AuditOrigin.Ui,
            new AuditActor(ActorKind.User, "haru", ActorProvider.Discord),
            AuditAction.ServerStart, AuditSeverity.Info,
            new AuditTarget(AuditTargetKind.Server, sid, sid), sid, AuthTestFactory.HostId, "old", null));
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStop, sid));

        string since = DateTimeOffset.UtcNow.AddDays(-1).ToString("o");
        JsonElement body = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&since={Uri.EscapeDataString(since)}"));
        Assert.Equal(1, body.GetProperty("data").GetArrayLength());
        Assert.Equal(AuditAction.ServerStop, body.GetProperty("data")[0].GetProperty("action").GetString());

        // a garbage since is ignored (no filter), never a silently empty page.
        JsonElement all = await Json(await Viewer().GetAsync($"/api/v1/audit?serverId={sid}&since=not-a-date"));
        Assert.Equal(2, all.GetProperty("data").GetArrayLength());
    }

    // --- category: the action-group prefix (server.* / backup.* …) ---------------------------------
    [Fact]
    public async Task GetAudit_Category_PrefixMatches()
    {
        string actor = $"cat-{Guid.NewGuid():N}";
        await Audit.AppendAsync(ServerWrite(AuditAction.ServerStart, $"s-{Guid.NewGuid():N}", actor));
        await Audit.AppendAsync(ServerWrite(AuditAction.BackupCreate, $"b-{Guid.NewGuid():N}", actor));

        JsonElement backups = await Json(await Viewer().GetAsync($"/api/v1/audit?actor={actor}&category=backup"));
        Assert.Equal(1, backups.GetProperty("data").GetArrayLength());
        Assert.Equal(AuditAction.BackupCreate, backups.GetProperty("data")[0].GetProperty("action").GetString());

        JsonElement servers = await Json(await Viewer().GetAsync($"/api/v1/audit?actor={actor}&category=server"));
        Assert.Equal(1, servers.GetProperty("data").GetArrayLength());
        Assert.Equal(AuditAction.ServerStart, servers.GetProperty("data")[0].GetProperty("action").GetString());
    }

    // --- The API-internal write path, end-to-end (no kgsm event): a real login -> auth.login row ---
    [Fact]
    public async Task Login_WritesAuthLoginAudit_Readable()
    {
        // Drive a real callback through the CSRF round-trip (the FakeDiscordResolver: code=operator).
        HttpClient login = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        string state = (await login.GetAsync("/auth/discord/start")).Headers.Location!.Query
            .TrimStart('?').Split('&').First(kv => kv.StartsWith("state=")).Substring("state=".Length);
        HttpResponseMessage cb = await login.GetAsync($"/auth/discord/callback?code=operator&state={state}");
        Assert.Equal(HttpStatusCode.OK, cb.StatusCode);

        // The login is an API-internal action (no kgsm event) — written directly, no double-write.
        JsonElement body = await Json(await Viewer().GetAsync("/api/v1/audit?actor=haru"));
        JsonElement[] rows = body.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(rows, x => x.GetProperty("action").GetString() == AuditAction.AuthLogin);
        JsonElement loginRow = rows.First(x => x.GetProperty("action").GetString() == AuditAction.AuthLogin);
        Assert.Equal("discord", loginRow.GetProperty("actor").GetProperty("provider").GetString());
        Assert.Equal("operator", loginRow.GetProperty("meta").GetProperty("tier").GetString());
        Assert.Equal(JsonValueKind.Null, loginRow.GetProperty("target").ValueKind); // panel-wide, no target
    }

    // --- The audit topic: an append is pushed as audit.append ---------------------------------------
    [Fact]
    public async Task AuditTopic_DeliversAppend()
    {
        using HttpResponseMessage resp = await SseTestHelpers.OpenStream(
            factory.CreateClient(), "/api/v1/stream?topics=audit", factory.AccessToken(AuthTier.Viewer));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using SseFrameReader frames = await SseTestHelpers.Frames(resp);

        // Append repeatedly across the read window so the subscription is certainly live before the
        // append we observe (no ack protocol; this avoids a subscribe/append race without sleeps).
        string sid = $"sse-{Guid.NewGuid():N}";
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        JsonElement? frame = null;
        while (DateTime.UtcNow < deadline)
        {
            await Audit.AppendAsync(ServerWrite(AuditAction.ServerStart, sid));
            JsonElement? got = await frames.WaitForFrame(
                f => f.GetProperty("type").GetString() == "audit.append", TimeSpan.FromMilliseconds(400));
            if (got is not null)
            {
                frame = got;
                break;
            }
        }

        Assert.NotNull(frame);
        JsonElement env = frame!.Value;
        Assert.Equal("audit", env.GetProperty("topic").GetString());
        Assert.Equal("audit.append", env.GetProperty("type").GetString());
        Assert.Equal(AuditAction.ServerStart, env.GetProperty("data").GetProperty("action").GetString());
    }
}
