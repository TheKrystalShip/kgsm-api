using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Unit tests for <see cref="ServicesPump"/> — the subscriber-gated systemd poll that emits
/// <c>service.patch</c> frames on the <c>hosts/{id}/services</c> topic.
/// </summary>
public sealed class ServicesPumpTests
{
    private static readonly LeafDescriptor WatchdogLeaf = LeafCatalog.Default.First(l => l.Id == "watchdog");
    private static readonly LeafDescriptor MonitorLeaf = LeafCatalog.Default.First(l => l.Id == "monitor");

    private static UnitState Active => new("active", "running", true,
        DateTimeOffset.UtcNow.AddMinutes(-5), 1234, 1024 * 1024);
    private static UnitState Inactive => new("inactive", "dead", true,
        DateTimeOffset.UtcNow.AddMinutes(-2), null, null);
    private static UnitState Failed => new("failed", "failed", true,
        DateTimeOffset.UtcNow.AddMinutes(-1), 5678, 2048);

    private static HostCapabilities ColdCaps => new(
        Metrics: new Capability(true, CapabilityStatus.Unknown),
        Assistant: new Capability(true, CapabilityStatus.Unknown),
        Watchdog: new Capability(true, CapabilityStatus.Unknown),
        Scheduler: new Capability(true, CapabilityStatus.Unknown),
        Reactor: new Capability(true, CapabilityStatus.Unknown));

    private static HostCapabilities AllOperational => new(
        Metrics: new Capability(true, CapabilityStatus.Operational),
        Assistant: new Capability(true, CapabilityStatus.Operational),
        Watchdog: new Capability(true, CapabilityStatus.Operational),
        Scheduler: new Capability(true, CapabilityStatus.Operational),
        Reactor: new Capability(true, CapabilityStatus.Operational));

    private static HostCapabilities MonitorDownCaps => new(
        Metrics: new Capability(true, CapabilityStatus.Down, Message: "Health check failed."),
        Assistant: new Capability(true, CapabilityStatus.Operational),
        Watchdog: new Capability(true, CapabilityStatus.Operational),
        Scheduler: new Capability(true, CapabilityStatus.Operational),
        Reactor: new Capability(true, CapabilityStatus.Operational));

    // --- BuildLeafService: state mapping ---

    [Fact]
    public void BuildLeafService_maps_systemd_state_fields()
    {
        var svc = Build(WatchdogLeaf, Active, ColdCaps);

        Assert.Equal("watchdog", svc.Id);
        Assert.Equal("Watchdog", svc.DisplayName);
        Assert.Equal("active", svc.State);
        Assert.Equal("running", svc.SubState);
        Assert.True(svc.Enabled);
        Assert.Equal(1234, svc.MainPid);
        Assert.Equal(1024 * 1024, svc.MemoryBytes);
        Assert.Equal("kgsm-watchdog.service", svc.Unit);
        Assert.False(svc.OnDemand);
    }

    [Fact]
    public void BuildLeafService_inactive_state_passes_through()
    {
        var svc = Build(WatchdogLeaf, Inactive, ColdCaps);

        Assert.Equal("inactive", svc.State);
        Assert.Equal("dead", svc.SubState);
        Assert.Null(svc.MainPid);
        Assert.Null(svc.MemoryBytes);
    }

    [Fact]
    public void BuildLeafService_failed_state_passes_through()
    {
        var svc = Build(WatchdogLeaf, Failed, ColdCaps);

        Assert.Equal("failed", svc.State);
        Assert.Equal(5678, svc.MainPid);
        Assert.Equal(2048, svc.MemoryBytes);
    }

    [Fact]
    public void BuildLeafService_unknown_state_passes_through()
    {
        var svc = Build(WatchdogLeaf, UnitState.Unknown, ColdCaps);

        Assert.Equal("unknown", svc.State);
        Assert.Null(svc.SubState);
        Assert.Null(svc.Enabled);
        Assert.Null(svc.MainPid);
        Assert.Null(svc.MemoryBytes);
    }

    // --- BuildLeafService: health mapping ---

    [Fact]
    public void BuildLeafService_maps_health_from_capability()
    {
        var svc = Build(MonitorLeaf, Active, MonitorDownCaps);

        Assert.NotNull(svc.Health);
        Assert.Equal(CapabilityStatus.Down, svc.Health!.Status);
        Assert.Equal("Health check failed.", svc.Health.Message);
    }

    [Fact]
    public void BuildLeafService_null_health_when_no_probe()
    {
        var firewall = LeafCatalog.Default.First(l => l.Id == "firewall");
        var svc = Build(firewall, Active, ColdCaps);

        Assert.Null(svc.Health);
    }

    [Fact]
    public void BuildLeafService_null_health_when_not_provisioned()
    {
        // When the capability is not provisioned, HealthFor returns null (not a probed down)
        var caps = new HostCapabilities(
            Metrics: new Capability(false, CapabilityStatus.Down), // not provisioned
            Assistant: new Capability(true, CapabilityStatus.Operational),
            Watchdog: new Capability(true, CapabilityStatus.Operational),
            Scheduler: new Capability(true, CapabilityStatus.Operational),
            Reactor: new Capability(true, CapabilityStatus.Operational));

        var svc = Build(MonitorLeaf, Active, caps);

        Assert.Null(svc.Health);
    }

    [Fact]
    public void BuildLeafService_operational_health_when_probe_succeeds()
    {
        var svc = Build(MonitorLeaf, Active, AllOperational);

        Assert.NotNull(svc.Health);
        Assert.Equal(CapabilityStatus.Operational, svc.Health!.Status);
        Assert.Null(svc.Health.Message);
    }

    // --- BuildLeafService: on-demand flag ---

    [Fact]
    public void BuildLeafService_on_demand_flag_from_catalog()
    {
        var firewall = LeafCatalog.Default.First(l => l.Id == "firewall");
        var svc = Build(firewall, Inactive, ColdCaps);

        Assert.True(svc.OnDemand);
    }

    [Fact]
    public void BuildLeafService_not_on_demand_for_resident_leaves()
    {
        var svc = Build(WatchdogLeaf, Active, ColdCaps);

        Assert.False(svc.OnDemand);
    }

    // --- BuildLeafService: provisioned flag ---

    [Fact]
    public void BuildLeafService_provisioned_null_for_non_provisionable_leaves()
    {
        // api and bot are not provisionable → provisioned is always null
        var api = LeafCatalog.Default.First(l => l.Id == "api");
        var bot = LeafCatalog.Default.First(l => l.Id == "bot");
        var scheduler = LeafCatalog.Default.First(l => l.Id == "scheduler");

        Assert.Null(Build(api, Active, ColdCaps).Provisioned);
        Assert.Null(Build(bot, Active, ColdCaps).Provisioned);
        Assert.Null(Build(scheduler, Active, ColdCaps).Provisioned);
    }

    [Fact]
    public void BuildLeafService_provisioned_true_when_resolver_returns_true()
    {
        LeafService svc = ServicesPump.BuildLeafService(MonitorLeaf, Active, ColdCaps, id => true);
        Assert.True(svc.Provisioned);
    }

    [Fact]
    public void BuildLeafService_provisioned_false_when_resolver_returns_false()
    {
        LeafService svc = ServicesPump.BuildLeafService(MonitorLeaf, Active, ColdCaps, id => false);
        Assert.False(svc.Provisioned);
    }

    [Fact]
    public void BuildLeafService_provisioned_null_when_resolver_is_null()
    {
        // If isProvisioned func is null, provisionable leaves get null (safe degrade)
        LeafService svc = ServicesPump.BuildLeafService(MonitorLeaf, Active, ColdCaps, (Func<string, bool>?)null);
        Assert.Null(svc.Provisioned);
    }

    // --- BuildLeafService: all catalog leaves ---

    [Fact]
    public void BuildLeafService_produces_valid_dto_for_every_catalog_leaf()
    {
        // Verify that BuildLeafService produces a valid LeafService for every leaf in the catalog
        foreach (LeafDescriptor leaf in LeafCatalog.Default)
        {
            var svc = Build(leaf, Active, ColdCaps);

            Assert.Equal(leaf.Id, svc.Id);
            Assert.Equal(leaf.DisplayName, svc.DisplayName);
            Assert.Equal(leaf.Unit, svc.Unit);
            Assert.Equal("active", svc.State);

            // Non-provisionable leaves must have null provisioned
            if (!ProvisionableLeaf.IsProvisionable(leaf.Id))
                Assert.Null(svc.Provisioned);
        }
    }

    // --- Wire shape serialization ---

    [Fact]
    public void ServicePatch_serializes_to_camelCase_wire_shape()
    {
        var hub = new StreamHub(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions
        {
            SerializerOptions = { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        }));
        string topic = StreamProtocol.HostServicesTopic("test-host");
        var svc = Build(MonitorLeaf, Active, ColdCaps);
        var msg = new StreamMessage(topic, StreamProtocol.ServicePatch, svc);

        string json = JsonSerializer.Serialize(msg, hub.Json);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("hosts/test-host/services", root.GetProperty("topic").GetString());
        Assert.Equal("service.patch", root.GetProperty("type").GetString());
        JsonElement data = root.GetProperty("data");
        Assert.Equal("monitor", data.GetProperty("id").GetString());
        Assert.Equal("active", data.GetProperty("state").GetString());
        Assert.Equal("kgsm-monitor.service", data.GetProperty("unit").GetString());
        Assert.Equal("Monitor", data.GetProperty("displayName").GetString());
    }

    [Fact]
    public void ServicePatch_omits_null_fields_via_condition()
    {
        var hub = new StreamHub(Options.Create(new Microsoft.AspNetCore.Http.Json.JsonOptions
        {
            SerializerOptions = { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        }));
        // Use the firewall leaf — LeafHealthSource.None, so health is always null
        var firewall = LeafCatalog.Default.First(l => l.Id == "firewall");
        var inactiveNoHealth = new UnitState("inactive", "dead", true,
            DateTimeOffset.UtcNow.AddMinutes(-2), null, null);
        var svc = ServicesPump.BuildLeafService(firewall, inactiveNoHealth, ColdCaps,
            (Func<string, bool>?)null);
        var msg = new StreamMessage("topic", "service.patch", svc);
        string json = JsonSerializer.Serialize(msg, hub.Json);

        using var doc = JsonDocument.Parse(json);
        JsonElement data = doc.RootElement.GetProperty("data");
        // SubState is "dead" (not null) → should be present
        Assert.True(data.TryGetProperty("subState", out _));
        // Enabled is true (not null) → should be present
        Assert.True(data.TryGetProperty("enabled", out _));
        // MainPid is null → should be omitted by [property: JsonIgnore(WhenWritingNull)]
        Assert.False(data.TryGetProperty("mainPid", out _));
        // MemoryBytes is null → should be omitted
        Assert.False(data.TryGetProperty("memoryBytes", out _));
        // Provisioned is null for firewall (non-provisionable via registry) → should be omitted
        Assert.False(data.TryGetProperty("provisioned", out _));
        // Health is null for LeafHealthSource.None → should be omitted
        var hasHealth = data.TryGetProperty("health", out _);
        Assert.False(hasHealth, $"health should be omitted but JSON is: {json}");
    }

    // --- StreamProtocol constant tests ---

    [Fact]
    public void HostServicesTopic_follows_naming_convention()
    {
        Assert.Equal("hosts/hotrod/services", StreamProtocol.HostServicesTopic("hotrod"));
        Assert.Equal("hosts/abc/services", StreamProtocol.HostServicesTopic("abc"));
    }

    [Fact]
    public void ServiceEntityKey_follows_naming_convention()
    {
        Assert.Equal("services:monitor", StreamProtocol.ServiceEntityKey("monitor"));
        Assert.Equal("services:watchdog", StreamProtocol.ServiceEntityKey("watchdog"));
    }

    [Fact]
    public void RequiresOperator_includes_services_topic()
    {
        Assert.True(StreamProtocol.RequiresOperator("hosts/hotrod/services"));
        Assert.True(StreamProtocol.RequiresOperator("hosts/any-host/services"));
    }

    [Fact]
    public void RequiresOperator_includes_logs_topic()
    {
        Assert.True(StreamProtocol.RequiresOperator("hosts/hotrod/logs"));
    }

    [Fact]
    public void RequiresOperator_does_not_include_metrics_or_capabilities()
    {
        Assert.False(StreamProtocol.RequiresOperator("hosts/hotrod/metrics"));
        Assert.False(StreamProtocol.RequiresOperator("hosts/hotrod/capabilities"));
    }

    [Fact]
    public void IsHostServicesTopic_matches_correctly()
    {
        Assert.True(StreamProtocol.IsHostServicesTopic("hosts/hotrod/services"));
        Assert.False(StreamProtocol.IsHostServicesTopic("hosts/hotrod/logs"));
        Assert.False(StreamProtocol.IsHostServicesTopic("hosts/hotrod/metrics"));
        Assert.False(StreamProtocol.IsHostServicesTopic("servers"));
        Assert.False(StreamProtocol.IsHostServicesTopic("hosts/hotrod/service")); // no trailing 's'
    }

    // --- Helpers ---

    /// <summary>Shorthand for BuildLeafService without needing a real LeafRegistry.</summary>
    private static LeafService Build(LeafDescriptor leaf, UnitState st, HostCapabilities caps) =>
        ServicesPump.BuildLeafService(leaf, st, caps, (Func<string, bool>?)null);
}
