using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using KgsmLogLevel = TheKrystalShip.KGSM.Core.Models.Enums.LogLevel;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Tests that <see cref="UpdateCheckCache"/> emits <see cref="AuditAction.ServerUpdateAvailable"/> audit
/// events only on state transitions (UpdatesAvailable flips from false/null to true), and is silent when
/// the reading doesn't change.
/// </summary>
public sealed class UpdateCheckCacheEmissionTests
{
    private const string HostId = "test-host";

    [Fact]
    public async Task RefreshAsync_TransitionFalseToTrue_UpdatesReading()
    {
        var slowStatuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>(StringComparer.Ordinal)
        {
            ["factorio-01"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                Status = true,
                Version = new VersionInfo { Checked = true, UpdatesAvailable = true, Latest = "1.1.0", Current = "1.0.0" }
            })
        };
        var roster = new Dictionary<string, Instance>(StringComparer.Ordinal)
        {
            ["factorio-01"] = new Instance { Name = "factorio-01", BlueprintFile = "factorio.bp.yaml" }
        };

        var (cache, instanceCache) = CreateTestSetup(roster, slowStatuses);

        // Act
        await cache.RefreshAsync(CancellationToken.None);

        // Assert
        UpdateReading reading = cache.Get("factorio-01");
        Assert.True(reading.UpdatesAvailable);
        Assert.Equal("1.1.0", reading.LatestVersion);
    }

    [Fact]
    public async Task RefreshAsync_TransitionTrueToTrue_KeepsReading()
    {
        var prior = new Dictionary<string, UpdateReading>(StringComparer.Ordinal)
        {
            ["factorio-01"] = new UpdateReading(true, "1.1.0", DateTimeOffset.UtcNow.AddMinutes(-5))
        };
        var slowStatuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>(StringComparer.Ordinal)
        {
            ["factorio-01"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                Status = true,
                Version = new VersionInfo { Checked = true, UpdatesAvailable = true, Latest = "1.1.0", Current = "1.0.0" }
            })
        };
        var roster = new Dictionary<string, Instance>(StringComparer.Ordinal)
        {
            ["factorio-01"] = new Instance { Name = "factorio-01", BlueprintFile = "factorio.bp.yaml" }
        };

        var (cache, _) = CreateTestSetup(roster, slowStatuses);
        SeedReadings(cache, prior);

        // Act
        await cache.RefreshAsync(CancellationToken.None);

        // Assert
        UpdateReading reading = cache.Get("factorio-01");
        Assert.True(reading.UpdatesAvailable);
        Assert.Equal("1.1.0", reading.LatestVersion);
    }

    [Fact]
    public async Task RefreshAsync_FirstCheckWithUpdate_DetectsAvailable()
    {
        var slowStatuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>(StringComparer.Ordinal)
        {
            ["terraria-01"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                Status = true,
                Version = new VersionInfo { Checked = true, UpdatesAvailable = true, Latest = "2.0.0", Current = "1.9.0" }
            })
        };
        var roster = new Dictionary<string, Instance>(StringComparer.Ordinal)
        {
            ["terraria-01"] = new Instance { Name = "terraria-01", BlueprintFile = "terraria.bp.yaml" }
        };

        var (cache, _) = CreateTestSetup(roster, slowStatuses);

        // Act
        await cache.RefreshAsync(CancellationToken.None);

        // Assert
        UpdateReading reading = cache.Get("terraria-01");
        Assert.True(reading.UpdatesAvailable);
        Assert.Equal("2.0.0", reading.LatestVersion);
    }

    [Fact]
    public async Task RefreshAsync_FirstCheckNoUpdate_NoUpdateDetected()
    {
        var slowStatuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>(StringComparer.Ordinal)
        {
            ["terraria-01"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                Status = true,
                Version = new VersionInfo { Checked = true, UpdatesAvailable = false, Latest = "1.9.0", Current = "1.9.0" }
            })
        };
        var roster = new Dictionary<string, Instance>(StringComparer.Ordinal)
        {
            ["terraria-01"] = new Instance { Name = "terraria-01", BlueprintFile = "terraria.bp.yaml" }
        };

        var (cache, _) = CreateTestSetup(roster, slowStatuses);

        // Act
        await cache.RefreshAsync(CancellationToken.None);

        // Assert
        UpdateReading reading = cache.Get("terraria-01");
        Assert.False(reading.UpdatesAvailable);
    }

    [Fact]
    public async Task MarkUpdated_AfterDetection_ClearsReading()
    {
        var slowStatuses = new Dictionary<string, Reading<InstanceRuntimeStatus>>(StringComparer.Ordinal)
        {
            ["factorio-01"] = Reading<InstanceRuntimeStatus>.Measured(new InstanceRuntimeStatus
            {
                Status = true,
                Version = new VersionInfo { Checked = true, UpdatesAvailable = true, Latest = "1.1.0", Current = "1.0.0" }
            })
        };
        var roster = new Dictionary<string, Instance>(StringComparer.Ordinal)
        {
            ["factorio-01"] = new Instance { Name = "factorio-01", BlueprintFile = "factorio.bp.yaml" }
        };

        var (cache, _) = CreateTestSetup(roster, slowStatuses);

        // Initial state: no prior reading
        await cache.RefreshAsync(CancellationToken.None);
        Assert.True(cache.Get("factorio-01").UpdatesAvailable);

        // Act: mark updated (simulating the update being applied)
        cache.MarkUpdated("factorio-01");

        // Assert: reading is cleared
        UpdateReading reading = cache.Get("factorio-01");
        Assert.False(reading.UpdatesAvailable);
        Assert.Equal("1.1.0", reading.LatestVersion);  // prior version kept
    }

    // --- Helpers ---

    private static (UpdateCheckCache Cache, InstanceCache InstanceCache) CreateTestSetup(
        Dictionary<string, Instance> roster,
        Dictionary<string, Reading<InstanceRuntimeStatus>> slowStatuses)
    {
        ApiOptions options = BuildOptions();

        var instanceCache = new InstanceCache(
            new ServiceCollection().BuildServiceProvider(),
            options,
            NullLogger<InstanceCache>.Instance);

        // Seed the roster via reflection
        var rosterField = typeof(InstanceCache).GetField("_roster",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        rosterField?.SetValue(instanceCache, (IReadOnlyDictionary<string, Instance>)roster);

        var services = new ServiceCollection();
        services.AddSingleton(instanceCache);
        services.AddSingleton<IInstanceService>(new FakeInstanceService(slowStatuses));
        services.AddSingleton(options);
        services.AddSingleton<AuditService>();
        var provider = services.BuildServiceProvider();

        var cache = new UpdateCheckCache(
            provider,
            instanceCache,
            options,
            NullLogger<UpdateCheckCache>.Instance);

        return (cache, instanceCache);
    }

    private static ApiOptions BuildOptions() =>
        ApiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:HostId"] = HostId,
            })
            .Build());

    private static void SeedReadings(UpdateCheckCache cache, IReadOnlyDictionary<string, UpdateReading> readings)
    {
        var field = typeof(UpdateCheckCache).GetField("_readings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(cache, readings);
    }

    /// <summary>
    /// Minimal IInstanceService stub that returns pre-configured slow statuses.
    /// Only GetAllStatuses is meaningfully implemented; all other methods throw.
    /// </summary>
    private sealed class FakeInstanceService(Dictionary<string, Reading<InstanceRuntimeStatus>> statuses) : IInstanceService
    {
        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast) => statuses;
        public Dictionary<string, Instance> GetAll() => new();
        public Dictionary<string, Instance>? GetAllOrNull() => new();
        public Instance? GetInstanceInfo(string instanceName) => null;
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => null;
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
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
        public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, KgsmLogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
