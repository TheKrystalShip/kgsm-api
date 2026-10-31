using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Cluster.Messaging;
using TheKrystalShip.Api.Services.Transfers;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// A clock a test moves by hand, so a ticket expires without anybody waiting for it to.
/// </summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Sends each request to the in-memory node its host names (<c>http://node-s</c>, <c>http://node-t</c>),
/// and answers <c>503</c> for a host marked down. One instance serves every named client of a node, so
/// the member calls, the bus and the part streams all cross the same way.
/// </summary>
public sealed class NodeRouter : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _nodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts that answer nothing.</summary>
    public ConcurrentDictionary<string, bool> Down { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Flips one byte of the next N part bodies read from any node.</summary>
    public int TamperParts;

    public void Add(string host, HttpMessageHandler handler) => _nodes[host] = new HttpMessageInvoker(handler, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string host = request.RequestUri!.Host;
        if (Down.ContainsKey(host) || !_nodes.TryGetValue(host, out HttpMessageInvoker? node))
            throw new HttpRequestException($"{host} is unreachable");

        HttpResponseMessage response = await node.SendAsync(request, ct);
        if (response.IsSuccessStatusCode
            && request.RequestUri.AbsolutePath.Contains("/parts/", StringComparison.Ordinal)
            && !request.RequestUri.AbsolutePath.EndsWith("/sha256", StringComparison.Ordinal)
            && Interlocked.Decrement(ref TamperParts) >= 0)
        {
            byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
            if (body.Length > 0) body[body.Length / 2] ^= 0xFF;
            var tampered = new ByteArrayContent(body);
            tampered.Headers.ContentType = response.Content.Headers.ContentType;
            response.Content = tampered;
        }

        return response;
    }

    // The node handlers belong to their TestServers; disposing a client must not dispose them.
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// The engine both nodes of a transfer drive, in memory: one stopped instance to export, the lock, the
/// parts as deterministic bytes, and the import's staging. Switch-on-input: each knob is set before the
/// test acts, and every call it receives is counted.
/// </summary>
public sealed class FakeTransferEngine : IInstanceService
{
    public const string EngineVersion = "3.18.0-rc34";

    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Instance> _roster = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _staged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _parts = new(StringComparer.Ordinal);
    private int _imports;

    public FakeTransferEngine(params string[] instances)
    {
        foreach (string id in instances) AddInstance(id);
        _parts["tree"] = Bytes(256 * 1024, 7);
        _parts["image-1"] = Bytes(64 * 1024, 11);
    }

    /// <summary>The refusals <c>import check</c> and <c>import begin</c> answer with.</summary>
    public List<ImportRefusal> Refusals { get; } = [];

    /// <summary>A non-zero exit <c>import finish</c> answers with instead of finishing.</summary>
    public int FinishExit { get; set; }

    /// <summary>How many more <c>export --commit</c> calls fail with a transient exit.</summary>
    public int CommitFailures;

    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentBag<string> Aborted { get; } = [];

    public void AddInstance(string id, bool running = false)
    {
        _roster[id] = new Instance
        {
            Name = id,
            BlueprintFile = "terraria.bp.yaml",
            Runtime = InstanceRuntime.Native,
            Library = "default",
            LibraryDir = "/opt",
            LibraryState = InstanceLibraryState.Online,
        };
        _running[id] = running;
    }

    public void Lock(string id, string token) => _locks[id] = token;
    public string? LockOf(string id) => _locks.TryGetValue(id, out string? t) ? t : null;
    public bool Has(string id) => _roster.ContainsKey(id);
    public void Stage(string importId) => _staged[importId] = "terraria";
    public bool IsStaged(string importId) => _staged.ContainsKey(importId);
    public int Count(string verb) => Calls.Count(c => c == verb);

    public Dictionary<string, Instance>? GetAllOrNull() => GetAll();
    public Dictionary<string, Instance> GetAll() => new(_roster, StringComparer.Ordinal);

    public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) =>
        _roster.Keys.ToDictionary(id => id, id => Reading<InstanceRuntimeStatus>.Measured(
            new InstanceRuntimeStatus { InstanceName = id, Status = _running.TryGetValue(id, out bool r) && r }),
            StringComparer.Ordinal);

    public TransferOutcome<TransferManifest> ExportManifest(string instanceName, string? lockToken = null, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("export-manifest");
        if (!_roster.ContainsKey(instanceName)) return new(new KgsmResult(25, "", "not found"), null);
        if (_running.TryGetValue(instanceName, out bool running) && running)
            return new(new KgsmResult(57, "", "This instance must be stopped first"), null);
        if (lockToken is not null)
        {
            string held = _locks.GetOrAdd(instanceName, lockToken);
            if (held != lockToken) return new(new KgsmResult(59, "", "locked by another transfer"), null);
        }

        return new(new KgsmResult(0), Manifest(instanceName));
    }

    public async Task<StreamResult> ExportPartAsync(string instanceName, string partId, string lockToken, Stream destination, string? actor = null, string? origin = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("export-part");
        if (LockOf(instanceName) != lockToken) return new StreamResult(59, "not locked by this token");
        if (!_parts.TryGetValue(partId, out byte[]? bytes)) return new StreamResult(25, "unknown part");

        for (int at = 0; at < bytes.Length; at += 16 * 1024)
            await destination.WriteAsync(bytes.AsMemory(at, Math.Min(16 * 1024, bytes.Length - at)), cancellationToken);
        return new StreamResult(0, "");
    }

    public KgsmResult CommitExport(string instanceName, string lockToken, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("commit");
        if (!_roster.ContainsKey(instanceName)) return new KgsmResult(25, "", "not found");
        if (LockOf(instanceName) != lockToken) return new KgsmResult(59, "", "not locked by this token");
        if (Interlocked.Decrement(ref CommitFailures) >= 0) return new KgsmResult(55, "", "library offline");

        _roster.TryRemove(instanceName, out _);
        _locks.TryRemove(instanceName, out _);
        return new KgsmResult(0);
    }

    public KgsmResult Unlock(string instanceName, string? lockToken = null, bool force = false, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("unlock");
        if (!_roster.ContainsKey(instanceName)) return new KgsmResult(25, "", "not found");
        if (!force && LockOf(instanceName) is { } held && held != lockToken) return new KgsmResult(59, "", "another token");
        _locks.TryRemove(instanceName, out _);
        return new KgsmResult(0);
    }

    public ImportVerdict ImportCheck(TransferManifest manifest, string? library = null, string? blueprintSuffix = null, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("import-check");
        return new ImportVerdict { Ok = Refusals.Count == 0, Refusals = [.. Refusals], Library = "default", Blueprint = manifest.Blueprint.Name };
    }

    public ImportBeginResult ImportBegin(TransferManifest manifest, string? library = null, string? blueprintSuffix = null, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("import-begin");
        if (Refusals.Count > 0)
            return new ImportBeginResult(null, ImportCheck(manifest, library, blueprintSuffix, actor, origin));

        string id = "imp-" + Interlocked.Increment(ref _imports).ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
        _staged[id] = manifest.Instance.Id;
        return new ImportBeginResult(id, null);
    }

    public async Task<StreamResult> ImportPartAsync(string importId, string partId, Stream source, string? actor = null, string? origin = null, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue("import-part");
        if (!_staged.ContainsKey(importId)) return new StreamResult(64, "unknown import");
        await source.CopyToAsync(Stream.Null, cancellationToken);
        return new StreamResult(0, "");
    }

    public TransferOutcome<ImportedInstance> ImportFinish(string importId, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("import-finish");
        if (!_staged.TryGetValue(importId, out string? source)) return new(new KgsmResult(64, "", "unknown import"), null);
        if (FinishExit != 0) return new(new KgsmResult(FinishExit, "", "the start-once proof failed"), null);

        _staged.TryRemove(importId, out _);
        string minted = source + "-arrived";
        AddInstance(minted);
        return new(new KgsmResult(0), new ImportedInstance { Instance = minted, Blueprint = "terraria", Library = "default" });
    }

    public KgsmResult ImportAbort(string importId, string? actor = null, string? origin = null)
    {
        Calls.Enqueue("import-abort");
        Aborted.Add(importId);
        _staged.TryRemove(importId, out _);
        return new KgsmResult(0);
    }

    public TransferOutcome<List<StagedImport>> ImportList() =>
        new(new KgsmResult(0), [.. _staged.Select(s => new StagedImport { ImportId = s.Key, Instance = s.Value, Library = "default", Blueprint = "terraria" })]);

    /// <summary>The hex SHA-256 of a part as the fake engine writes it.</summary>
    public string PartHash(string partId) => Convert.ToHexString(SHA256.HashData(_parts[partId])).ToLowerInvariant();

    private TransferManifest Manifest(string id) => new()
    {
        Format = 1,
        EngineVersion = EngineVersion,
        Instance = new TransferInstance
        {
            Id = id, Blueprint = "terraria", Runtime = "native", DisplayName = "Survival", Autostart = true,
            HasRun = true, Library = "default", WorkingDir = "/opt/instances/terraria/" + id,
            Ports = [new TransferPort { Start = 7777, End = 7777, Protocol = "tcp" }],
        },
        Blueprint = new TransferBlueprint { Name = "terraria", Custom = false, Sha256 = "abc" },
        Requirements = ["steamcmd"],
        Parts =
        [
            new TransferPart { Id = "tree", Bytes = _parts["tree"].Length, Files = 12 },
            new TransferPart { Id = "image-1", Bytes = _parts["image-1"].Length, Ref = "x/y:1", ImageId = "sha256:1" },
        ],
    };

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // --- not part of a transfer ---
    public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
    public KgsmResult Install(string blueprintName, string? library = null, string? version = null,
        string? displayName = null, string? actor = null, string? origin = null, int? port = null,
        bool? start = null, string? id = null) => throw new NotImplementedException();
    public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
    public List<InstanceBackup> GetBackupsDetailed(string instanceName) => [];
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
    public KgsmResult SetMaintenanceWindows(string instanceName, string packedWindows, string? author, string? actor = null, string? origin = null, string? authorName = null) => throw new NotImplementedException();
    public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}

/// <summary>
/// Two cluster nodes in one process, a source (<c>node-s</c>) and a target (<c>node-t</c>), each with its
/// own database, journal and fake engine, each holding the other as an enabled member, and every call
/// between them — the member routes, the part streams and the bus — crossing the real HTTP pipeline
/// through <see cref="NodeRouter"/>.
/// </summary>
public sealed class TransferPair : IAsyncDisposable
{
    public const string SourceId = "node-s";
    public const string TargetId = "node-t";
    public const string Instance = "terraria";

    public FakeTransferEngine SourceEngine { get; } = new(Instance, "running-1");
    public FakeTransferEngine TargetEngine { get; } = new();
    public ManualTime SourceTime { get; } = new(DateTimeOffset.UtcNow);
    public NodeRouter Router { get; } = new();
    public ClusterNodeFactory Source { get; }
    public ClusterNodeFactory Target { get; }

    private readonly string _dbS = Path.Combine(Path.GetTempPath(), $"kgsm-api-xfer-s-{Guid.NewGuid():N}.db");
    private readonly string _dbT = Path.Combine(Path.GetTempPath(), $"kgsm-api-xfer-t-{Guid.NewGuid():N}.db");

    private TransferPair(string secret)
    {
        SourceEngine.AddInstance("running-1", running: true);
        Source = Node(SourceId, _dbS, secret, SourceEngine, SourceTime);
        Target = Node(TargetId, _dbT, secret, TargetEngine, TimeProvider.System);
    }

    public static async Task<TransferPair> StartAsync()
    {
        var pair = new TransferPair("transfer-pair-" + Guid.NewGuid().ToString("N"));
        pair.Router.Add(SourceId, pair.Source.Server.CreateHandler());
        pair.Router.Add(TargetId, pair.Target.Server.CreateHandler());

        await Know(pair.Source, TargetId);
        await Know(pair.Target, SourceId);

        // Both stores load and both roster reads settle before a test acts.
        foreach (ClusterNodeFactory node in new[] { pair.Source, pair.Target })
        {
            await node.Services.GetRequiredService<TransferStore>().LoadAsync();
            Services.Aggregation.InstanceCache cache = node.Services.GetRequiredService<Services.Aggregation.InstanceCache>();
            await Until(() => Task.FromResult(cache.Loaded));
        }

        return pair;
    }

    public TransferSource SourceSide => Source.Services.GetRequiredService<TransferSource>();
    public TransferTarget TargetSide => Target.Services.GetRequiredService<TransferTarget>();
    public TransferStore SourceStore => Source.Services.GetRequiredService<TransferStore>();
    public TransferStore TargetStore => Target.Services.GetRequiredService<TransferStore>();

    public HttpClient Client(ClusterNodeFactory node, Persona? persona = Persona.Owner)
    {
        HttpClient c = node.CreateClient();
        if (persona is { } p)
            c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", node.AccessToken(p));
        return c;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, or fails the test after ten seconds.</summary>
    public static async Task Until(Func<Task<bool>> condition, string? what = null)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException("condition never held: " + (what ?? "(unnamed)"));
            await Task.Delay(50);
        }
    }

    private ClusterNodeFactory Node(string id, string db, string secret, FakeTransferEngine engine, TimeProvider time) =>
        new(id, id, secret, dbPath: db,
            // Membership timers far beyond any test, so neither node's view of the other moves under it.
            gossipMs: 600_000, suspectMs: 600_000, reapMs: 1_200_000, pollMs: 600_000,
            configureServices: services =>
            {
                services.RemoveAll<IInstanceService>();
                services.AddSingleton<IInstanceService>(engine);
                services.RemoveAll<TransferClock>();
                services.AddSingleton(new TransferClock(time));
                services.AddHttpClient(TransferPeerClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Router);
                services.AddHttpClient(OutboxDrainer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Router);
            });

    private static async Task Know(ClusterNodeFactory node, string peer)
    {
        KGSM.Cluster.Membership.MembersStore members = node.Services.GetRequiredService<KGSM.Cluster.Membership.MembersStore>();
        await members.UpsertAsync(KGSM.Cluster.Membership.MemberRow.New(peer, KGSM.Cluster.Membership.MemberKind.Node) with
        {
            Url = "http://" + peer,
            Status = "alive",
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        await Target.DisposeAsync();
        foreach (string db in new[] { _dbS, _dbT })
        {
            foreach (string f in new[] { db, Path.ChangeExtension(db, ".cluster.db") })
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
    }
}
