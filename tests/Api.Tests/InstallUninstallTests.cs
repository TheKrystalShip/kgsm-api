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
/// M8·b coverage for the create/delete write path — <c>POST /servers</c> (install) and
/// <c>DELETE /servers/{id}</c> (uninstall), proven through the real pipeline with the engine seam faked
/// (a switch-on-input <see cref="FakeInstanceService"/>). The load-bearing contract is the GATE, asserted
/// synchronously on the HTTP response (the happy-path execution mutates the host and is a trusted-host
/// live-validate, like M3): missing/unknown blueprint → <c>400</c>, unknown server → <c>404</c>,
/// engine-unprovisioned → <c>503</c>, a valid request → <c>202</c> + a <c>{ job }</c> whose verb is
/// <c>install</c>/<c>uninstall</c>; operator-gated (viewer → <c>403</c>, no bearer → <c>401</c>). The
/// no-double-write invariant is proven too: a completed install writes NO audit row from the API
/// (kgsm owns <c>server.install</c> via the event echo; the fake engine emits none, so <c>/audit</c> stays
/// empty — a stray direct write would show up).
/// </summary>
public sealed class InstallUninstallTests
    : IClassFixture<InstallUninstallTests.EngineTestFactory>, IClassFixture<AuthTestFactory>
{
    private readonly EngineTestFactory _engine;   // a fake engine is registered → the gate's happy/sad branches
    private readonly AuthTestFactory _noEngine;   // engine unprovisioned → the 503 degrade

    public InstallUninstallTests(EngineTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // --- install (POST /servers) -------------------------------------------------------------------

    [Fact]
    public async Task Install_MissingBlueprint_400()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_UnknownBlueprint_400()
    {
        // The fake's generate-id rejects "zzznope" (the EC_BLUEPRINT_NOT_FOUND analog) → a client-input 400,
        // with kgsm's real detail surfaced — nothing is created.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator, "{\"blueprint\":\"zzznope\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_BadOrigin_400()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_TypeMismatchedBody_400_Envelope_NotProblemDetails()
    {
        // A typed reserved field with the wrong type trips [ApiController]'s model validation BEFORE the
        // action runs. It must STILL return the frozen { error } envelope (invariant #4), never the
        // framework's ValidationProblemDetails — the gotcha the api CLAUDE.md flags for M8's typed bodies.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"port\":\"not-a-number\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":", body);
        Assert.Contains("\"code\":\"bad_request\"", body);
        Assert.DoesNotContain("tools.ietf.org", body);   // NOT ProblemDetails
    }

    [Fact]
    public async Task Install_MalformedJson_400_Envelope()
    {
        // An unparseable body is also a pre-action model-binding failure — same envelope, never ProblemDetails.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator, "{not valid json");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"bad_request\"", body);
        Assert.DoesNotContain("tools.ietf.org", body);
    }

    [Fact]
    public async Task Install_Valid_202_InstallJob_NoAuditDoubleWrite()
    {
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator, "{\"blueprint\":\"factorio\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("install", job.GetProperty("verb").GetString());
        Assert.Equal("queued", job.GetProperty("state").GetString());
        // The backend assigned the id (generate-id echoed the generated name); the job is keyed to it.
        Assert.Equal("factorio-ab12", job.GetProperty("serverId").GetString());

        // No double-write: install is the echo path (kgsm owns server.install). The fake engine emits no
        // event and the API writes no row directly, so the audit feed stays empty — a stray direct write
        // by the command runner would surface here.
        HttpResponseMessage audit = await Client(_engine, KgsmTier.Viewer).GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.Empty(page.RootElement.GetProperty("data").EnumerateArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public async Task Install_PortOutOfRange_400(int port)
    {
        // The Game Port override is validated 1-65535 up front — an out-of-range value is a client-input
        // 400, never passed to kgsm to fail mid-install.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            $"{{\"blueprint\":\"factorio\",\"port\":{port}}}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_ValidPort_202()
    {
        // An in-range Game Port is accepted (and forwarded to the engine — see RunInstall → Install(port:)).
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"port\":34250}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("install", job.GetProperty("verb").GetString());
    }

    [Fact]
    public async Task Install_FreeTextName_DerivesPathSafeId()
    {
        // The label is what a create form asks for. The id is derived from it as a courtesy — a slug the
        // engine validated and echoed back — so the job is keyed on something a person recognises rather
        // than on `factorio-NN`. The label itself never has to survive the charset.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"name\":\"Sunday Server!\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("sunday-server", job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Install_NameWithNoUsableSlug_FallsBackToGeneratedId()
    {
        // A label written entirely outside the id charset yields no slug, and that is not a reason to
        // refuse a create: the engine mints its own id and the label rides along untouched.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"name\":\"\\u65e5\\u66dc\\u30b5\\u30fc\\u30d0\\u30fc\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("factorio-ab12", job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Install_NameCollidingWithTheRoster_FallsBackToGeneratedId()
    {
        // The derived slug is a courtesy, so a collision falls through to the engine's generated id
        // rather than failing a create nobody asked to be picky about. (The fake roster holds factorio-1.)
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"name\":\"Factorio 1\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("factorio-ab12", job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Install_ExplicitId_IsHonored()
    {
        // A caller that must know the id in advance names it, and gets exactly that id.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            "{\"blueprint\":\"factorio\",\"id\":\"factorio-x1\",\"name\":\"Sunday Server\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("factorio-x1", job.GetProperty("serverId").GetString());
    }

    [Theory]
    [InlineData("bad id")]          // a space is not in the charset
    [InlineData("-leading")]        // must start alphanumeric
    [InlineData("factorio-1")]      // already on the roster
    public async Task Install_UnusableExplicitId_400(string id)
    {
        // An id the CALLER named is answered honestly — never silently adjusted into a different one,
        // which is the whole reason a caller names it.
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Operator,
            $"{{\"blueprint\":\"factorio\",\"id\":\"{id}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_Viewer_403()
    {
        // Operator-gated: a viewer reading /servers cannot create one. (Gate is orthogonal to permissions.)
        HttpResponseMessage resp = await Post(_engine, KgsmTier.Viewer, "{\"blueprint\":\"factorio\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Install_NoToken_401()
    {
        HttpResponseMessage resp = await Post(_engine, tier: null, "{\"blueprint\":\"factorio\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_EngineUnprovisioned_503()
    {
        // Past the blueprint/origin checks, an unconfigured engine degrades to 503 — not a 500.
        HttpResponseMessage resp = await Post(_noEngine, KgsmTier.Operator, "{\"blueprint\":\"factorio\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    // --- uninstall (DELETE /servers/{id}) ----------------------------------------------------------

    [Fact]
    public async Task Uninstall_UnknownServer_404()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Operator, "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uninstall_KnownServer_202_UninstallJob()
    {
        // The fake roster carries "factorio-1" (see FakeInstanceService.GetAll) → the gate admits it.
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Operator, "factorio-1");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("uninstall", job.GetProperty("verb").GetString());
        Assert.Equal("factorio-1", job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Uninstall_BadOrigin_400()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Operator, "factorio-1", origin: "hacker");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uninstall_Viewer_403()
    {
        HttpResponseMessage resp = await Delete(_engine, KgsmTier.Viewer, "factorio-1");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Uninstall_NoToken_401()
    {
        HttpResponseMessage resp = await Delete(_engine, tier: null, "factorio-1");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"unauthorized\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uninstall_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Delete(_noEngine, KgsmTier.Operator, "anything");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, KgsmTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Post(AuthTestFactory factory, KgsmTier? tier, string json) =>
        Client(factory, tier).PostAsync("/api/v1/servers",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Delete(
        AuthTestFactory factory, KgsmTier? tier, string id, string? origin = null) =>
        Client(factory, tier).DeleteAsync(
            $"/api/v1/servers/{id}" + (origin is null ? "" : $"?origin={origin}"));

    /// <summary>
    /// <see cref="AuthTestFactory"/> with a fake <see cref="IInstanceService"/> registered, so the
    /// install/uninstall gate exercises its real branches (generate-id, the roster lookup) without a live
    /// kgsm. Everything else (auth, routing, the command runner) is the production pipeline.
    /// </summary>
    public sealed class EngineTestFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakeInstanceService());
            });
        }
    }

    /// <summary>
    /// Switch-on-input fake (the project convention, like <c>FakeDiscordResolver</c>): no mutable per-call
    /// state. <c>generate-id</c> rejects a sentinel "zzznope" blueprint, applies the engine's own id
    /// charset and roster check to a proposed id, and otherwise returns a deterministic generated id;
    /// install/uninstall succeed; the roster carries one instance so the uninstall gate has something to
    /// admit — and so a proposed id colliding with it is refused the way the engine refuses it.
    /// </summary>
    private sealed class FakeInstanceService : IInstanceService
    {
        // The engine's id charset, so a test asserting that a bad id is refused is asserting against the
        // same rule the engine applies rather than against a stand-in that happens to agree today.
        private static readonly System.Text.RegularExpressions.Regex IdFormat =
            new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        public KgsmResult GenerateId(string blueprintName, string? id = null)
        {
            if (string.Equals(blueprintName, "zzznope", StringComparison.Ordinal))
                return new KgsmResult(27, "", $"Blueprint '{blueprintName}' not found or invalid");

            if (id is null)
                return new KgsmResult(0, $"{blueprintName}-ab12");

            if (!IdFormat.IsMatch(id))
                return new KgsmResult(2, "", $"Invalid instance id '{id}'");

            return GetAll().ContainsKey(id)
                ? new KgsmResult(2, "", $"Instance '{id}' already exists")
                : new KgsmResult(0, id);
        }

        // Accepts the Game Port override (the runner forwards it via Install(port:)); the gate tests assert
        // the 202 synchronously, the out-of-range rejection is asserted on the controller before this runs.
        public KgsmResult Install(string blueprintName, string? library = null, string? version = null,
            string? displayName = null, string? actor = null, string? origin = null, int? port = null,
            bool? start = null, string? id = null) => new(0);

        public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) =>
            throw new NotImplementedException();

        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => new(0);
        public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => throw new NotImplementedException();

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
        public Dictionary<string, Instance> GetAll() => new()
        {
            ["factorio-1"] = new Instance { Name = "factorio-1", BlueprintFile = "factorio.bp.yaml" },
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new();

        // --- unused by the M8·b gate: honest NotImplemented (never silently fabricate) ---
        public Instance? GetInstanceInfo(string instanceName) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null, string? reason = null, string? retention = null) => throw new NotImplementedException();
        public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public List<InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult Announce(string instanceName, string message, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
