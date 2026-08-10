using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.Api.Services.Backups;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Coverage for the backup-download surface — <c>POST /servers/{id}/backups/{backupId}/download-ticket</c>
/// and the ticket-authenticated <c>GET .../archive</c>.
/// </summary>
/// <remarks>
/// The ticket is the authorisation for bytes leaving the host, so what is asserted here is mostly what
/// must NOT work: an absent ticket, a ticket for a different backup, an expired one, and a viewer holding
/// a valid bearer. The engine seam is faked (<see cref="FakeBackups"/>) — the jail itself is kgsm-lib's
/// and is proven in <c>InstanceBackupsTests</c> against a real temp-dir jail.
/// </remarks>
public sealed class BackupDownloadTests
    : IClassFixture<BackupDownloadTests.BackupsTestFactory>, IClassFixture<AuthTestFactory>
{
    private const string Server = "factorio-1";        // in the fake roster
    private const string Compressed = "factorio-1-20260808T120000Z-aaaaaa";
    private const string Uncompressed = "factorio-1-20260808T130000Z-bbbbbb";
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("archive bytes, faithfully served");

    private readonly BackupsTestFactory _engine;
    private readonly AuthTestFactory _noEngine;

    public BackupDownloadTests(BackupsTestFactory engine, AuthTestFactory noEngine)
    {
        _engine = engine;
        _noEngine = noEngine;
    }

    // ===== minting =================================================================================

    [Fact]
    public async Task Mint_Operator_200_WithRelativeUrlSizeAndDigest()
    {
        JsonElement body = await MintOk(Server, Compressed);

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("ticket").GetString()));
        Assert.Equal(Payload.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("cafe1234", body.GetProperty("sha256").GetString());

        // Server-RELATIVE: the SPA drives a cluster and resolves each node's origin itself. An absolute
        // URL built here would have to guess which of this host's addresses the browser can reach.
        string url = body.GetProperty("url").GetString()!;
        Assert.StartsWith("/api/v1/servers/", url, StringComparison.Ordinal);
        Assert.Contains("/archive?ticket=", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mint_Viewer_403()
    {
        // A backup is the instance's whole install + saves — every secret the file browser is
        // operator-gated for, in one file. Listing backups stays viewer; taking one home does not.
        HttpResponseMessage resp = await Mint(_engine, KgsmTier.Viewer, Server, Compressed);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Mint_NoToken_401()
    {
        HttpResponseMessage resp = await Mint(_engine, null, Server, Compressed);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Mint_UncompressedBackup_409_Uncompressed()
    {
        HttpResponseMessage resp = await Mint(_engine, KgsmTier.Operator, Server, Uncompressed);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("backup_uncompressed", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mint_UnknownBackup_404()
    {
        // Refused at MINT rather than at download: a ticket for something unservable would surface as a
        // broken download two clicks later, with nothing to explain it.
        HttpResponseMessage resp = await Mint(_engine, KgsmTier.Operator, Server, "no-such-backup");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Mint_UnknownServer_404()
    {
        HttpResponseMessage resp = await Mint(_engine, KgsmTier.Operator, "no-such-server", Compressed);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Mint_EngineUnprovisioned_503()
    {
        HttpResponseMessage resp = await Mint(_noEngine, KgsmTier.Operator, Server, Compressed);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ===== redeeming ===============================================================================

    [Fact]
    public async Task Download_WithTicket_200_ServesTheBytesAndTheDigestHeader()
    {
        JsonElement minted = await MintOk(Server, Compressed);
        string url = minted.GetProperty("url").GetString()!;

        // NO bearer — the whole point is that a navigation carries none.
        HttpResponseMessage resp = await _engine.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(Payload, await resp.Content.ReadAsByteArrayAsync());

        Assert.Equal("cafe1234", resp.Headers.GetValues("X-Backup-Sha256").Single());
        Assert.Equal("application/gzip", resp.Content.Headers.ContentType?.MediaType);
        // Named for the backup, not data.tar.gz — every archive shares that filename, so a user keeping
        // several would otherwise collect data(3).tar.gz.
        Assert.Equal($"{Compressed}.tar.gz", resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Download_NoTicket_401()
    {
        HttpResponseMessage resp = await _engine.CreateClient()
            .GetAsync($"/api/v1/servers/{Server}/backups/{Compressed}/archive");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("invalid_ticket", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Download_GarbageTicket_401()
    {
        HttpResponseMessage resp = await _engine.CreateClient()
            .GetAsync($"/api/v1/servers/{Server}/backups/{Compressed}/archive?ticket=deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Download_TicketForAnotherBackup_401()
    {
        // THE authorisation property: a ticket names one backup. Without this check, a ticket for a
        // backup an operator may take would serve as a ticket for every other one.
        JsonElement minted = await MintOk(Server, Compressed);
        string handle = minted.GetProperty("ticket").GetString()!;

        HttpResponseMessage resp = await _engine.CreateClient()
            .GetAsync($"/api/v1/servers/{Server}/backups/{Uncompressed}/archive?ticket={handle}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Download_TicketForAnotherServer_401()
    {
        JsonElement minted = await MintOk(Server, Compressed);
        string handle = minted.GetProperty("ticket").GetString()!;

        HttpResponseMessage resp = await _engine.CreateClient()
            .GetAsync($"/api/v1/servers/valheim-1/backups/{Compressed}/archive?ticket={handle}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ===== the ticket store's own semantics =========================================================

    [Fact]
    public void Ticket_ExpiresAfterItsTtl()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var store = new BackupDownloadTickets(clock);

        (string handle, _) = store.Mint("s", "b", "discord:haru", "ui", "sid-1");
        Assert.True(store.TryRedeem(handle, "s", "b", out _, out bool first));
        Assert.True(first);

        clock.Advance(BackupDownloadTickets.Ttl + TimeSpan.FromSeconds(1));
        Assert.False(store.TryRedeem(handle, "s", "b", out _, out _));
    }

    [Fact]
    public void Ticket_IsRedeemableMoreThanOnce_ButAuditsOnlyOnFirst()
    {
        // Deliberately NOT single-use: a resumed or ranged download is a second request for the same
        // bytes, and burning the ticket on first contact would make the resumability this whole design
        // exists for impossible. The audit-once latch is what keeps one download to one row.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero));
        var store = new BackupDownloadTickets(clock);

        (string handle, _) = store.Mint("s", "b", "discord:haru", "ui", "sid-1");

        Assert.True(store.TryRedeem(handle, "s", "b", out _, out bool first));
        Assert.True(first);
        Assert.True(store.TryRedeem(handle, "s", "b", out _, out bool second));
        Assert.False(second);
        Assert.True(store.TryRedeem(handle, "s", "b", out _, out bool third));
        Assert.False(third);
    }

    [Fact]
    public void Ticket_CarriesTheMintersProvenance()
    {
        // The redeeming request is anonymous, so the row would otherwise have no actor. The ticket is
        // what carries who asked for it across the two requests.
        var store = new BackupDownloadTickets();
        (string handle, BackupDownloadTicket minted) = store.Mint("s", "b", "discord:haru", "ui", "sid-1");

        Assert.True(store.TryRedeem(handle, "s", "b", out BackupDownloadTicket? redeemed, out _));
        Assert.Equal("discord:haru", redeemed!.Actor);
        Assert.Equal("ui", redeemed.Origin);
        Assert.Equal("sid-1", redeemed.SessionId);
        Assert.Equal(minted.ExpiresAt, redeemed.ExpiresAt);
    }

    // ===== harness ==================================================================================

    private async Task<JsonElement> MintOk(string server, string backup)
    {
        HttpResponseMessage resp = await Mint(_engine, KgsmTier.Operator, server, backup);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Mint(
        AuthTestFactory f, KgsmTier? tier, string server, string backup)
    {
        HttpClient c = f.CreateClient();
        if (tier is { } t)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", f.AccessToken(t));
        return c.PostAsync($"/api/v1/servers/{server}/backups/{backup}/download-ticket",
            new StringContent("""{"origin":"ui"}""", Encoding.UTF8, "application/json"));
    }

    /// <summary>A clock the ticket tests can move without sleeping.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    public sealed class BackupsTestFactory : AuthTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInstanceBackups>();
                services.AddSingleton<IInstanceBackups>(new FakeBackups());
                // The controller's own existence pre-check reads the roster, so an engine that lists no
                // servers 404s every request before the backup service is ever consulted.
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(new FakeRoster());
            });
        }
    }

    /// <summary>
    /// Two backups: one compressed (servable) and one uncompressed (refused). Everything else is a
    /// miss, so the unknown-backup path is exercised by the same fake.
    /// </summary>
    private sealed class FakeBackups : IInstanceBackups
    {
        public FileOpResult<BackupArchive> OpenArchive(string instance, string backupId)
        {
            if (!string.Equals(instance, Server, StringComparison.Ordinal))
                return FileOpResult<BackupArchive>.Fail(FileOpOutcome.NotFound);

            if (string.Equals(backupId, Uncompressed, StringComparison.Ordinal))
                return FileOpResult<BackupArchive>.Fail(FileOpOutcome.NotAFile);

            if (!string.Equals(backupId, Compressed, StringComparison.Ordinal))
                return FileOpResult<BackupArchive>.Fail(FileOpOutcome.NotFound);

            return FileOpResult<BackupArchive>.Ok(new BackupArchive(
                new MemoryStream(Payload, writable: false),
                "data.tar.gz",
                Payload.Length,
                new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
                "cafe1234"));
        }
    }

    /// <summary>
    /// A two-server roster, which is all the controller's existence pre-check needs. The archives
    /// themselves come from <see cref="FakeBackups"/>.
    /// </summary>
    private sealed class FakeRoster : IInstanceService
    {
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

        // The download path never lists backups — it opens one archive through IInstanceBackups — so
        // these stay honestly unimplemented rather than returning a fabricated empty list.
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();

        // --- unused by this endpoint: honest NotImplemented (never silently fabricate) ---
        public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null, bool? start = null) => throw new NotImplementedException();
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
        public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
