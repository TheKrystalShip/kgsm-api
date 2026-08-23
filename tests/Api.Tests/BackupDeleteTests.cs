using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for <c>DELETE /servers/{id}/backups/{backupId}</c>.
/// </summary>
/// <remarks>
/// Deleting a backup cannot be undone, so most of what matters here is the refusals: the operator gate,
/// the engine's own verdict on an id it does not list, and the in-flight interlock that keeps a delete
/// from running while a restore is reading the very bytes it removes. The engine seam is faked — which id
/// is a real backup is kgsm's to decide, and it is proven there; what this asserts is that the API relays
/// that verdict instead of forming its own.
/// </remarks>
public sealed class BackupDeleteTests : IClassFixture<BackupDeleteTests.DeleteTestFactory>
{
    private const string Server = "factorio-1";        // in the fake roster
    private const string Backup = "factorio-1-20260808T120000Z-aaaaaa";

    private readonly DeleteTestFactory _f;

    public BackupDeleteTests(DeleteTestFactory f) => _f = f;

    [Fact]
    public async Task Operator_204_AndCallsTheEngineWithProvenance()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        HttpResponseMessage resp = await Delete(KgsmTier.Operator, Server, Backup, origin: "ui");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // The row this produces is kgsm's (instance_backup_deleted → backup.delete), so the actor and
        // surface have to reach the engine — they are stamped onto the command, not onto a row here.
        Assert.Equal(1, engine.Calls);
        Assert.Equal(Server, engine.LastInstance);
        Assert.Equal(Backup, engine.LastBackup);
        Assert.Equal("ui", engine.LastOrigin);
        Assert.False(string.IsNullOrWhiteSpace(engine.LastActor));
    }

    [Fact]
    public async Task Delete_DefaultsOriginToApi()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(KgsmTier.Operator, Server, Backup)).StatusCode);
        Assert.Equal("api", engine.LastOrigin);
    }

    [Fact]
    public async Task Delete_UnknownOrigin_400_AndNeverReachesTheEngine()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        HttpResponseMessage resp = await Delete(KgsmTier.Operator, Server, Backup, origin: "cron");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Delete_SystemOrigin_400()
    {
        // "system" is reserved for the engine's own autonomous actions; a caller claiming it would put a
        // human's deletion in the feed as something the host did to itself.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Delete(KgsmTier.Operator, Server, Backup, origin: "system")).StatusCode);
    }

    [Fact]
    public async Task Delete_Viewer_403()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(KgsmTier.Viewer, Server, Backup)).StatusCode);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Delete_NoToken_401()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Delete(null, Server, Backup)).StatusCode);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Delete_UnknownServer_404_AndNeverReachesTheEngine()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        Assert.Equal(HttpStatusCode.NotFound, (await Delete(KgsmTier.Operator, "no-such-server", Backup)).StatusCode);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Delete_EngineRefusesTheId_404_CarryingItsOwnMessage()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();
        engine.Result = new KgsmResult(1, "", "Backup 'nope' not found for instance 'factorio-1'");

        HttpResponseMessage resp = await Delete(KgsmTier.Operator, Server, "nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        // The engine owns the name set, so it owns the explanation too — never a guess made up here.
        Assert.Contains("not found", error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_EngineThrows_503()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();
        engine.Throw = new InvalidOperationException("kgsm is gone");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Delete(KgsmTier.Operator, Server, Backup)).StatusCode);
    }

    [Fact]
    public async Task Delete_WhileACommandIsInFlight_409_AndNeverReachesTheEngine()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        // A restore reads the directory a delete unlinks. The slot is what makes the two exclusive, so
        // hold it exactly as a restore would and prove the delete is turned away rather than racing it.
        JobRegistry jobs = _f.Services.GetRequiredService<JobRegistry>();
        Job? held = jobs.TryStart("job_holds", Server, CommandVerb.BackupRestore, DateTimeOffset.UtcNow);
        Assert.NotNull(held);
        try
        {
            HttpResponseMessage resp = await Delete(KgsmTier.Operator, Server, Backup);
            Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
            Assert.Equal(0, engine.Calls);
        }
        finally
        {
            jobs.Update(held! with { State = JobState.Succeeded, SettledAt = DateTimeOffset.UtcNow });
        }
    }

    [Fact]
    public async Task Delete_ReleasesTheSlot_SoTheNextOneSucceeds()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();

        // A synchronous verb that claims the slot and forgets to settle it would wedge the server for
        // every later command, and the symptom (a permanent 409 on start) would point nowhere near here.
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(KgsmTier.Operator, Server, Backup)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(KgsmTier.Operator, Server, Backup)).StatusCode);
        Assert.Equal(2, engine.Calls);

        Assert.Null(_f.Services.GetRequiredService<JobRegistry>().InFlightFor(Server));
    }

    [Fact]
    public async Task Delete_AFailedDeleteAlsoReleasesTheSlot()
    {
        FakeDeleter engine = _f.Engine;
        engine.Reset();
        engine.Result = new KgsmResult(1, "", "no such backup");

        Assert.Equal(HttpStatusCode.NotFound, (await Delete(KgsmTier.Operator, Server, Backup)).StatusCode);
        Assert.Null(_f.Services.GetRequiredService<JobRegistry>().InFlightFor(Server));
    }

    [Fact]
    public async Task Delete_NoEngineProvisioned_503()
    {
        using var noEngine = new AuthTestFactory();
        HttpClient c = noEngine.CreateClient();
        c.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", noEngine.AccessToken(KgsmTier.Operator));

        HttpResponseMessage resp = await c.DeleteAsync($"/api/v1/servers/{Server}/backups/{Backup}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ===== harness ==================================================================================

    private Task<HttpResponseMessage> Delete(KgsmTier? tier, string server, string backup, string? origin = null)
    {
        HttpClient c = _f.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.AccessToken(t));

        string url = $"/api/v1/servers/{server}/backups/{backup}";
        if (origin is not null) url += $"?origin={Uri.EscapeDataString(origin)}";
        return c.DeleteAsync(url);
    }

    public sealed class DeleteTestFactory : AuthTestFactory
    {
        public FakeDeleter Engine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(Engine);
            });
        }
    }

    /// <summary>
    /// A roster of two servers whose <see cref="DeleteBackup"/> records the call and answers with
    /// whatever <see cref="Result"/> is set to, so the controller's relaying of the engine's verdict can
    /// be asserted without a host to delete anything on.
    /// </summary>
    public sealed class FakeDeleter : IInstanceService
    {
        public int Calls { get; private set; }
        public string? LastInstance { get; private set; }
        public string? LastBackup { get; private set; }
        public string? LastActor { get; private set; }
        public string? LastOrigin { get; private set; }
        public KgsmResult Result { get; set; } = new(0);
        public Exception? Throw { get; set; }

        public void Reset()
        {
            Calls = 0;
            LastInstance = LastBackup = LastActor = LastOrigin = null;
            Result = new KgsmResult(0);
            Throw = null;
        }

        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null)
        {
            Calls++;
            LastInstance = instanceName;
            LastBackup = backupName;
            LastActor = actor;
            LastOrigin = origin;
            if (Throw is { } ex) throw ex;
            return Result;
        }

        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();

        public Dictionary<string, Instance> GetAll() => new()
        {
            [Server] = new Instance { Name = Server, BlueprintFile = "factorio.bp.yaml" },
            ["valheim-1"] = new Instance { Name = "valheim-1", BlueprintFile = "valheim.bp.yaml" },
        };

        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) =>
            GetAll().ToDictionary(
                kv => kv.Key,
                kv => Reading<InstanceRuntimeStatus>.Measured(
                    new InstanceRuntimeStatus { InstanceName = kv.Key, Status = false }));

        public Instance? GetInstanceInfo(string instanceName) => GetAll().GetValueOrDefault(instanceName);

        // --- unused by this endpoint: honest NotImplemented (never silently fabricate) ---
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null, bool? start = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null, string? reason = null, string? retention = null) => throw new NotImplementedException();
        public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public List<InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
