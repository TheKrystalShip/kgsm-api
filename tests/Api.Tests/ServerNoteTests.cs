using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The server-note surface — <c>GET /servers/{id}/note</c> (viewer read), <c>PUT</c> (operator write)
/// and <c>DELETE</c> (operator clear) — proven through the real pipeline with the engine seam faked.
/// The load-bearing contracts: the tier split (a viewer reads but cannot write), an over-cap body is
/// rejected rather than truncated, an empty PUT is refused so an emptied editor can't silently wipe a
/// note, a clear keeps the note attributable, and a partial engine failure reports which keys landed
/// instead of feigning atomicity.
/// </summary>
public sealed class ServerNoteTests
    : IClassFixture<ServerNoteTests.NoteEngineFactory>, IClassFixture<AuthTestFactory>
{
    private readonly NoteEngineFactory _engine;
    private readonly AuthTestFactory _noEngine;   // engine unprovisioned → the 503 degrade

    public ServerNoteTests(NoteEngineFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // --- GET ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_NoToken_401()
    {
        HttpResponseMessage resp = await Get(_engine, tier: null, "noted");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_NoneTier_403()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.None, "noted");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Get_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Get(_noEngine, KgsmTier.Viewer, "noted");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_UnknownServer_404()
    {
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_Viewer_ReadsNoteWithAttribution()
    {
        // A viewer can READ the note (only writing is operator-gated) — it is player-facing content.
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "noted");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement note = doc.RootElement.GetProperty("note");
        Assert.Equal("noted", doc.RootElement.GetProperty("serverId").GetString());
        Assert.Equal("Modpack v2.4 — see #mods", note.GetProperty("body").GetString());
        Assert.Equal("discord:cristian", note.GetProperty("updatedBy").GetString());
        Assert.Equal("2026-08-02T14:31:07.0000000Z", note.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task Get_ServerWithoutNote_NoteIsNull()
    {
        // Honest "nothing written" — not an empty-string placeholder.
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "blank");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Get_HandEditedNote_RendersLiterallyWithNullAttribution()
    {
        // Someone typed a plain note into .config.ini: the body still renders, and the author is an
        // honest null rather than a fabricated one.
        HttpResponseMessage resp = await Get(_engine, KgsmTier.Viewer, "handwritten");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement note = doc.RootElement.GetProperty("note");
        Assert.Equal("be nice, no griefing", note.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, note.GetProperty("updatedBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, note.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task Get_NoteAlsoRidesTheServerListDto()
    {
        // The dashboard tile renders the note, so a list row must carry it without a detail fetch.
        HttpClient client = Client(_engine, KgsmTier.Viewer);
        HttpResponseMessage resp = await client.GetAsync("/api/v1/servers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement noted = doc.RootElement.EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == "noted");
        Assert.Equal("Modpack v2.4 — see #mods", noted.GetProperty("note").GetProperty("body").GetString());
    }

    // --- PUT ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Put_NoToken_401()
    {
        HttpResponseMessage resp = await Put(_engine, tier: null, "noted", "{\"body\":\"hi\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Put_Viewer_403()
    {
        // Operator-gated: a viewer reads the note but cannot rewrite a player-facing message.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Viewer, "noted", "{\"body\":\"hi\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Put_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Put(_noEngine, KgsmTier.Operator, "noted", "{\"body\":\"hi\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task Put_UnknownServer_404()
    {
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "does-not-exist", "{\"body\":\"hi\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_BadOrigin_400()
    {
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "noted",
            "{\"body\":\"hi\",\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"body\":\"\"}")]
    [InlineData("{\"body\":\"   \\n \"}")]
    public async Task Put_EmptyBody_400_PointsAtDelete(string json)
    {
        // Clearing is DELETE — an accidentally-emptied editor must not silently wipe the note.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "noted", json);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("DELETE", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Put_OverTheCap_400_NotTruncated()
    {
        string body = new('a', InstanceNote.MaxLength + 1);
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "noted",
            JsonSerializer.Serialize(new { body }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string payload = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"bad_request\"", payload);
        Assert.Contains("601", payload);
    }

    [Fact]
    public async Task Put_AtTheCap_IsAccepted()
    {
        string body = new('a', InstanceNote.MaxLength);
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "noted",
            JsonSerializer.Serialize(new { body }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Put_CapIsMeasuredAfterSanitizing()
    {
        // Control characters are stripped before storage, so they must not cost the operator characters
        // against the cap.
        string body = new string('a', InstanceNote.MaxLength) + "\t\t\t";
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "noted",
            JsonSerializer.Serialize(new { body }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Put_Operator_WritesThroughTheNoteApi_NotRawConfig()
    {
        // The note must go through SetInstanceNote (which owns the encoding + the attribution stamp),
        // never a raw config-set that would drop an unencoded body into a sourced file.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "writable",
            "{\"body\":\"Rules: be nice\",\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        RecordingInstanceService fake = _engine.Fake;
        Assert.Equal("Rules: be nice", fake.LastNoteBody);
        Assert.Equal("writable", fake.LastNoteInstance);
        Assert.Equal("ui", fake.LastNoteOrigin);
        Assert.NotNull(fake.LastNoteActor);   // the bearer identity, stamped for the audit echo
    }

    [Fact]
    public async Task Put_EngineRefusesAKey_500_ReportsWhatApplied()
    {
        // kgsm writes one key per call, so a mid-sequence refusal is a genuine partial state — the
        // response says which keys landed rather than implying nothing changed.
        HttpResponseMessage resp = await Put(_engine, KgsmTier.Operator, "refuses", "{\"body\":\"hi\"}");
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("engine_refused", error.GetProperty("code").GetString());

        JsonElement details = error.GetProperty("details");
        Assert.Equal("note", details.GetProperty("failedKey").GetString());
        string[] applied = details.GetProperty("applied").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["note_updated_by", "note_updated_at"], applied);
    }

    // --- DELETE ------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_Viewer_403()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Viewer, "noted");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownServer_404()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Operator, "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_ClearsTheBodyButStaysAttributable()
    {
        // The clear is an empty-body write: attribution still records who removed the note and when.
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Operator, "writable");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        RecordingInstanceService fake = _engine.Fake;
        Assert.Equal(string.Empty, fake.LastNoteBody);
        Assert.NotNull(fake.LastNoteActor);
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Get(AuthTestFactory factory, KgsmTier? tier, string id) =>
        Client(factory, tier).GetAsync($"/api/v1/servers/{id}/note");

    private static Task<HttpResponseMessage> Put(AuthTestFactory factory, KgsmTier? tier, string id, string json) =>
        Client(factory, tier).PutAsync($"/api/v1/servers/{id}/note",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Delete(AuthTestFactory factory, KgsmTier? tier, string id) =>
        Client(factory, tier).DeleteAsync($"/api/v1/servers/{id}/note");

    /// <summary>
    /// <see cref="AuthTestFactory"/> with a recording fake <see cref="IInstanceService"/>, so the note
    /// path exercises its real branches (roster gate, info read, note write) without a live kgsm.
    /// </summary>
    public class NoteEngineFactory : AuthTestFactory
    {
        public RecordingInstanceService Fake { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(Fake);
                services.RemoveAll<IWatchdogClient>();
            });
        }
    }

    /// <summary>
    /// A roster of four instances covering every note state — one written through a surface (encoded
    /// body + attribution), one with no note, one hand-edited (a literal, unencoded value), and one
    /// whose engine refuses the body key so the partial-write branch is reachable. Note writes are
    /// recorded rather than applied; the read side stays fixed so the fake needs no locking.
    /// </summary>
    public sealed class RecordingInstanceService : IInstanceService
    {
        public string? LastNoteInstance { get; private set; }
        public string? LastNoteBody { get; private set; }
        public string? LastNoteActor { get; private set; }
        public string? LastNoteOrigin { get; private set; }

        private static Instance Noted() => new()
        {
            Name = "noted",
            BlueprintFile = "factorio.bp.yaml",
            Note = InstanceNote.Encode("Modpack v2.4 — see #mods"),
            NoteUpdatedBy = "discord:cristian",
            NoteUpdatedAt = "2026-08-02T14:31:07Z",
        };

        private static Instance Blank() => new() { Name = "blank", BlueprintFile = "factorio.bp.yaml" };

        private static Instance Handwritten() => new()
        {
            Name = "handwritten",
            BlueprintFile = "factorio.bp.yaml",
            Note = "be nice, no griefing",   // never encoded — someone edited .config.ini directly
        };

        private static Instance Writable() => new() { Name = "writable", BlueprintFile = "factorio.bp.yaml" };

        private static Instance Refuses() => new() { Name = "refuses", BlueprintFile = "factorio.bp.yaml" };

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();

        public Dictionary<string, Instance> GetAll() => new()
        {
            ["noted"] = Noted(),
            ["blank"] = Blank(),
            ["handwritten"] = Handwritten(),
            ["writable"] = Writable(),
            ["refuses"] = Refuses(),
        };

        public Instance? GetInstanceInfo(string instanceName) => instanceName switch
        {
            "noted" => Noted(),
            "blank" => Blank(),
            "handwritten" => Handwritten(),
            "writable" => Writable(),
            "refuses" => Refuses(),
            _ => null,
        };

        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null)
        {
            LastNoteInstance = instanceName;
            LastNoteBody = body;
            LastNoteActor = actor;
            LastNoteOrigin = origin;

            // "refuses" fails on the body key — attribution has already landed, the note has not.
            return instanceName == "refuses"
                ? new InstanceNoteResult(false,
                    [InstanceNote.UpdatedByKey, InstanceNote.UpdatedAtKey],
                    InstanceNote.BodyKey, "the engine refused 'note'", 8)
                : new InstanceNoteResult(true,
                    [InstanceNote.UpdatedByKey, InstanceNote.UpdatedAtKey, InstanceNote.BodyKey]);
        }

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- unused by the note path: honest NotImplemented (never silently fabricate) ---
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null, bool? start = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName, bool emit = false) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
