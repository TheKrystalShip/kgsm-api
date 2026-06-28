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
/// HTTP-contract + auth coverage for the file browser (<c>GET/PUT /servers/{id}/files…</c>, plan §7.7),
/// proven through the real pipeline with the engine seam faked at a real temp jail. The load-bearing
/// assertions: the operator gate (viewer <c>403</c>, no token <c>401</c> — both read AND write, since
/// contents hold secrets), the degrade codes (unknown <c>404</c>, engine-absent <c>503</c>), the binary
/// <c>409</c>, the save <c>412</c>, and the secret-hygiene regression — the <c>file.write</c> audit row
/// carries path/size/sha256 but <strong>NEVER the content</strong>.
/// </summary>
public sealed class FileBrowserApiTests
    : IClassFixture<FileBrowserApiTests.FilesTestFactory>, IClassFixture<AuthTestFactory>
{
    private const string Server = "fb-1";

    private readonly FilesTestFactory _engine;
    private readonly AuthTestFactory _noEngine;

    public FileBrowserApiTests(FilesTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // ===== auth gate (operator for read AND write) =================================================

    [Fact]
    public async Task List_NoToken_401()
    {
        HttpResponseMessage r = await Client(_engine, tier: null).GetAsync($"/api/v1/servers/{Server}/files");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task List_Viewer_403_ReadIsOperatorPlus()
    {
        // Even LISTING is operator+ (file contents routinely hold secrets) — a viewer is forbidden.
        HttpResponseMessage r = await Client(_engine, AuthTier.Viewer).GetAsync($"/api/v1/servers/{Server}/files");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Save_Viewer_403()
    {
        HttpResponseMessage r = await Put(_engine, AuthTier.Viewer, "edit.cfg", "{\"content\":\"x\"}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ===== degrade ================================================================================

    [Fact]
    public async Task List_UnknownServer_404()
    {
        HttpResponseMessage r = await Client(_engine, AuthTier.Operator).GetAsync("/api/v1/servers/nope/files");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task List_EngineUnprovisioned_503()
    {
        HttpResponseMessage r = await Client(_noEngine, AuthTier.Operator).GetAsync($"/api/v1/servers/{Server}/files");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    // ===== happy path (list + read) ===============================================================

    [Fact]
    public async Task List_Root_200_DirsFirst()
    {
        HttpResponseMessage r = await Client(_engine, AuthTier.Operator).GetAsync($"/api/v1/servers/{Server}/files");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        JsonElement root = doc.RootElement;
        Assert.Equal("", root.GetProperty("path").GetString());
        JsonElement entries = root.GetProperty("entries");
        Assert.Equal("dir", entries[0].GetProperty("kind").GetString());           // dirs first
        // a dir omits editable/lang; a text file carries editable:true
        Assert.False(entries[0].TryGetProperty("editable", out _));
        JsonElement cfg = entries.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "server.cfg");
        Assert.True(cfg.GetProperty("editable").GetBoolean());
        Assert.Equal("cfg", cfg.GetProperty("lang").GetString());
    }

    [Fact]
    public async Task Read_TextFile_200()
    {
        HttpResponseMessage r = await Client(_engine, AuthTier.Operator)
            .GetAsync($"/api/v1/servers/{Server}/files/content?path=server.cfg");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("utf-8", doc.RootElement.GetProperty("encoding").GetString());
        Assert.StartsWith("sha256:", doc.RootElement.GetProperty("etag").GetString());
        Assert.Contains("name=", doc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Read_Binary_409_FileBinary()
    {
        HttpResponseMessage r = await Client(_engine, AuthTier.Operator)
            .GetAsync($"/api/v1/servers/{Server}/files/content?path=blob.bin");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("\"code\":\"file_binary\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Read_TraversalEscape_404()
    {
        HttpResponseMessage r = await Client(_engine, AuthTier.Operator)
            .GetAsync($"/api/v1/servers/{Server}/files/content?path=../../../../etc/passwd");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ===== save (round-trip + 412 + 404) + the secret-hygiene audit row ===========================

    [Fact]
    public async Task Save_RoundTrip_200_AndAuditRowHasNoContent()
    {
        // A unique secret in the content — it must NOT appear in the audit row (only path/size/sha256).
        const string secret = "rcon_password=SUPER_SECRET_TOKEN_42";
        HttpResponseMessage put = await Put(_engine, AuthTier.Operator, "edit.cfg",
            JsonSerializer.Serialize(new { content = secret + "\n", origin = "ui" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using JsonDocument result = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.StartsWith("sha256:", result.RootElement.GetProperty("etag").GetString());

        // The file.write audit row exists, scoped to the server, with path/size/sha256 meta — and the
        // raw audit response NEVER contains the secret content (the regression that matters most).
        HttpResponseMessage auditResp = await Client(_engine, AuthTier.Operator).GetAsync("/api/v1/audit?serverId=" + Server);
        string auditJson = await auditResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SUPER_SECRET_TOKEN_42", auditJson);

        using JsonDocument page = JsonDocument.Parse(auditJson);
        JsonElement row = page.RootElement.GetProperty("data").EnumerateArray()
            .First(e => e.GetProperty("action").GetString() == "file.write");
        JsonElement meta = row.GetProperty("meta");
        Assert.Equal("edit.cfg", meta.GetProperty("path").GetString());
        Assert.True(meta.TryGetProperty("sizeBytes", out _));
        Assert.StartsWith("sha256:", meta.GetProperty("sha256").GetString());
        Assert.False(meta.TryGetProperty("content", out _));  // never, ever
        Assert.Equal("ui", row.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Save_StaleEtag_412()
    {
        HttpResponseMessage r = await Put(_engine, AuthTier.Operator, "edit.cfg",
            JsonSerializer.Serialize(new { content = "x\n", etag = "sha256:deadbeef" }));
        Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode);
    }

    [Fact]
    public async Task Save_NonExistent_404_NoCreate()
    {
        HttpResponseMessage r = await Put(_engine, AuthTier.Operator, "does-not-exist.cfg",
            JsonSerializer.Serialize(new { content = "x" }));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Save_MissingContent_400()
    {
        HttpResponseMessage r = await Put(_engine, AuthTier.Operator, "edit.cfg", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Save_BadOrigin_400()
    {
        HttpResponseMessage r = await Put(_engine, AuthTier.Operator, "edit.cfg",
            "{\"content\":\"x\",\"origin\":\"hacker\"}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ===== helpers ================================================================================

    private static HttpClient Client(AuthTestFactory factory, AuthTier? tier)
    {
        HttpClient c = factory.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.AccessToken(t));
        return c;
    }

    private static Task<HttpResponseMessage> Put(AuthTestFactory f, AuthTier? tier, string path, string json) =>
        Client(f, tier).PutAsync($"/api/v1/servers/{Server}/files/content?path={Uri.EscapeDataString(path)}",
            new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary><see cref="AuthTestFactory"/> with a fake <see cref="IInstanceService"/> whose
    /// <c>WorkingDir</c> points at a real per-factory temp jail (created + seeded here) — so the file
    /// endpoints exercise their real filesystem I/O without a live kgsm.</summary>
    public sealed class FilesTestFactory : AuthTestFactory, IDisposable
    {
        public string Jail { get; } = Path.Combine(Path.GetTempPath(), "fbapi-" + Guid.NewGuid().ToString("N")[..10]);

        public FilesTestFactory()
        {
            Directory.CreateDirectory(Jail);
            Directory.CreateDirectory(Path.Combine(Jail, "install"));
            File.WriteAllText(Path.Combine(Jail, "server.cfg"), "name=krystal\nport=27015\n");
            File.WriteAllText(Path.Combine(Jail, "edit.cfg"), "original\n");
            File.WriteAllBytes(Path.Combine(Jail, "blob.bin"), [0x00, 0x01, 0x02, 0xFF]);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakeFilesInstanceService(Jail));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { try { Directory.Delete(Jail, recursive: true); } catch { /* best-effort */ } }
        }
    }

    /// <summary>Roster of one instance whose <c>WorkingDir</c> is the temp jail; everything else is the
    /// honest NotImplemented (never silently fabricate).</summary>
    private sealed class FakeFilesInstanceService(string jail) : IInstanceService
    {
        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
        public Dictionary<string, Instance> GetAll() => new()
        {
            [Server] = new Instance { Name = Server, BlueprintFile = "factorio.bp.yaml", WorkingDir = jail },
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => new()
        {
            [Server] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus { InstanceName = Server, Status = false }),
        };

        public Instance? GetInstanceInfo(string instanceName) =>
            string.Equals(instanceName, Server, StringComparison.Ordinal)
                ? new Instance { Name = Server, BlueprintFile = "factorio.bp.yaml", WorkingDir = jail }
                : null;

        // --- unused by the file surface ---
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
