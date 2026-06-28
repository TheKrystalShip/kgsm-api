using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
/// HTTP-contract + auth coverage for the console-input write (<c>POST /servers/{id}/console</c>), proven
/// through the real pipeline with the engine seam faked. Load-bearing assertions: the operator gate
/// (no token <c>401</c>, viewer <c>403</c>), the native-only gate (a container is <c>409</c>), the degrade
/// codes (unknown id <c>404</c>, engine-absent <c>503</c>), input validation (<c>400</c>), and the
/// fire-and-forget success (<c>202</c>) vs the engine "not running" failure surfaced honestly (<c>409</c>
/// with kgsm's own message). The console.input audit row itself is the engine ECHO (kgsm emits
/// instance_input_sent; <see cref="Services.Audit.AuditMapping.FromInputSentEvent"/> maps it) — covered by
/// the mapper unit tests, not written by this controller, so there is nothing to assert here for it.
/// </summary>
public sealed class ConsoleApiTests
    : IClassFixture<ConsoleApiTests.ConsoleTestFactory>, IClassFixture<AuthTestFactory>
{
    private const string Native = "con-1";       // a native instance — console input is supported
    private const string Container = "con-ctr";  // a container instance — console input is native-only

    private readonly ConsoleTestFactory _engine;
    private readonly AuthTestFactory _noEngine;

    public ConsoleApiTests(ConsoleTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // ===== auth gate (operator — at least as privileged as a lifecycle command) ====================

    [Fact]
    public async Task Post_NoToken_401()
    {
        HttpResponseMessage r = await Post(_engine, tier: null, Native, "{\"input\":\"/say hi\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Post_Viewer_403_WriteIsOperatorPlus()
    {
        HttpResponseMessage r = await Post(_engine, AuthTier.Viewer, Native, "{\"input\":\"/say hi\"}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ===== happy path ==============================================================================

    [Fact]
    public async Task Post_Operator_Native_202_Delivered()
    {
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, Native, "{\"input\":\"/say hello\",\"origin\":\"ui\"}");
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode); // 202 — delivered to the console input
    }

    // ===== native-only gate ========================================================================

    [Fact]
    public async Task Post_Operator_Container_409_NativeOnly()
    {
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, Container, "{\"input\":\"/say hi\"}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("native", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ===== degrade + validation ====================================================================

    [Fact]
    public async Task Post_Operator_UnknownId_404()
    {
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, "nope", "{\"input\":\"/say hi\"}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Post_EngineAbsent_503()
    {
        // The AuthTestFactory leaves the engine unprovisioned → no IInstanceService → honest 503.
        HttpResponseMessage r = await Post(_noEngine, AuthTier.Operator, Native, "{\"input\":\"/say hi\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    [Fact]
    public async Task Post_BlankInput_400()
    {
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, Native, "{\"input\":\"   \"}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Post_OverLongInput_400()
    {
        string big = new string('x', 1001);
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, Native, $"{{\"input\":\"{big}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Post_BadOrigin_400()
    {
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, Native, "{\"input\":\"/say hi\",\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ===== engine "not running" surfaced honestly ==================================================

    [Fact]
    public async Task Post_EngineReportsNotRunning_409_SurfacesKgsmMessage()
    {
        // The fake fails the sentinel command exactly as kgsm would when there's no FIFO (server stopped):
        // the controller must surface that, not fabricate a success.
        HttpResponseMessage r = await Post(_engine, AuthTier.Operator, Native, "{\"input\":\"/trigger-fail\"}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("No active server", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ===== helpers ================================================================================

    private static Task<HttpResponseMessage> Post(AuthTestFactory f, AuthTier? tier, string id, string json)
    {
        HttpClient c = f.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(t));
        return c.PostAsync($"/api/v1/servers/{id}/console",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    /// <summary><see cref="AuthTestFactory"/> with a fake <see cref="IInstanceService"/> exposing one native
    /// and one container instance; <c>SendInput</c> succeeds except for a sentinel command that fails like a
    /// stopped server (no FIFO).</summary>
    public sealed class ConsoleTestFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakeConsoleInstanceService());
            });
        }
    }

    private sealed class FakeConsoleInstanceService : IInstanceService
    {
        public Instance? GetInstanceInfo(string instanceName) => instanceName switch
        {
            Native => new Instance { Name = Native, BlueprintFile = "factorio.bp.yaml", Runtime = InstanceRuntime.Native },
            Container => new Instance { Name = Container, BlueprintFile = "vrising.bp.yaml", Runtime = InstanceRuntime.Container },
            _ => null,
        };

        // Switch-on-input (parallel-safe, no mutable capture): the sentinel fails like a stopped server;
        // everything else succeeds (exit 0).
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) =>
            command == "/trigger-fail"
                ? new KgsmResult(1, "", "Input failed: No active server found.")
                : new KgsmResult(0, "", "");

        // --- unused by the console surface ---
        public Dictionary<string, Instance>? GetAllOrNull() => throw new NotImplementedException();
        public Dictionary<string, Instance> GetAll() => throw new NotImplementedException();
        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
