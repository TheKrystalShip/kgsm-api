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
        HttpResponseMessage resp = await Post(_engine, AuthTier.Operator, "{}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_UnknownBlueprint_400()
    {
        // The fake's generate-id rejects "zzznope" (the EC_BLUEPRINT_NOT_FOUND analog) → a client-input 400,
        // with kgsm's real detail surfaced — nothing is created.
        HttpResponseMessage resp = await Post(_engine, AuthTier.Operator, "{\"blueprint\":\"zzznope\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_BadOrigin_400()
    {
        HttpResponseMessage resp = await Post(_engine, AuthTier.Operator,
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
        HttpResponseMessage resp = await Post(_engine, AuthTier.Operator,
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
        HttpResponseMessage resp = await Post(_engine, AuthTier.Operator, "{not valid json");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"bad_request\"", body);
        Assert.DoesNotContain("tools.ietf.org", body);
    }

    [Fact]
    public async Task Install_Valid_202_InstallJob_NoAuditDoubleWrite()
    {
        HttpResponseMessage resp = await Post(_engine, AuthTier.Operator, "{\"blueprint\":\"factorio\"}");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("install", job.GetProperty("verb").GetString());
        Assert.Equal("queued", job.GetProperty("state").GetString());
        // The backend assigned the id (generate-id echoed the generated name); the job is keyed to it.
        Assert.Equal("factorio-ab12", job.GetProperty("serverId").GetString());

        // No double-write: install is the echo path (kgsm owns server.install). The fake engine emits no
        // event and the API writes no row directly, so the audit feed stays empty — a stray direct write
        // by the command runner would surface here.
        HttpResponseMessage audit = await Client(_engine, AuthTier.Viewer).GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        using JsonDocument page = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.Empty(page.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Install_Viewer_403()
    {
        // Operator-gated: a viewer reading /servers cannot create one. (Gate is orthogonal to permissions.)
        HttpResponseMessage resp = await Post(_engine, AuthTier.Viewer, "{\"blueprint\":\"factorio\"}");
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
        HttpResponseMessage resp = await Post(_noEngine, AuthTier.Operator, "{\"blueprint\":\"factorio\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    // --- uninstall (DELETE /servers/{id}) ----------------------------------------------------------

    [Fact]
    public async Task Uninstall_UnknownServer_404()
    {
        HttpResponseMessage resp = await Delete(_engine, AuthTier.Operator, "does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("\"code\":\"not_found\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uninstall_KnownServer_202_UninstallJob()
    {
        // The fake roster carries "factorio-1" (see FakeInstanceService.GetAll) → the gate admits it.
        HttpResponseMessage resp = await Delete(_engine, AuthTier.Operator, "factorio-1");
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        JsonElement job = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("job");
        Assert.Equal("uninstall", job.GetProperty("verb").GetString());
        Assert.Equal("factorio-1", job.GetProperty("serverId").GetString());
    }

    [Fact]
    public async Task Uninstall_BadOrigin_400()
    {
        HttpResponseMessage resp = await Delete(_engine, AuthTier.Operator, "factorio-1", origin: "hacker");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("\"code\":\"bad_request\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Uninstall_Viewer_403()
    {
        HttpResponseMessage resp = await Delete(_engine, AuthTier.Viewer, "factorio-1");
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
        HttpResponseMessage resp = await Delete(_noEngine, AuthTier.Operator, "anything");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("\"code\":\"unavailable\"", await resp.Content.ReadAsStringAsync());
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static HttpClient Client(AuthTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Post(AuthTestFactory factory, AuthTier? tier, string json) =>
        Client(factory, tier).PostAsync("/api/v1/servers",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<HttpResponseMessage> Delete(
        AuthTestFactory factory, AuthTier? tier, string id, string? origin = null) =>
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
    /// state. <c>generate-id</c> rejects a sentinel "zzznope" blueprint and otherwise echoes a custom name
    /// or returns a deterministic generated id; install/uninstall succeed; the roster carries one instance
    /// so the uninstall gate has something to admit.
    /// </summary>
    private sealed class FakeInstanceService : IInstanceService
    {
        public KgsmResult GenerateId(string blueprintName, string? customName = null) =>
            string.Equals(blueprintName, "zzznope", StringComparison.Ordinal)
                ? new KgsmResult(27, "", $"Blueprint '{blueprintName}' not found or invalid")
                : new KgsmResult(0, customName ?? $"{blueprintName}-ab12");

        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null,
            string? name = null, string? actor = null, string? origin = null) => new(0);

        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => new(0);

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
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
