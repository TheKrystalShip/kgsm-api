using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="MonitorEventShaping"/> — the read-time half of event-history-plan.md Phase C: turning one
/// of kgsm-monitor's raw, neutral <c>GET /events</c> rows into the exact same <see cref="AuditRecord"/>
/// shape <see cref="TheKrystalShip.Api.Services.Audit.KgsmAuditConsumer"/> used to persist at write time,
/// by reusing the very same <see cref="AuditMapping"/> <c>From*Event</c> mappers.
/// </summary>
public sealed class MonitorEventShapingTests
{
    private const string HostId = "h1";
    private static readonly DateTimeOffset Ts = new(2026, 7, 18, 10, 30, 0, TimeSpan.Zero);

    private static JsonElement Data(object o) => JsonSerializer.SerializeToElement(o);

    // --- Fidelity: a mapped type shapes to BYTE-IDENTICAL JSON as the write-time mapper would have ----
    [Fact]
    public void Shape_MappedType_MatchesTheWriteTimeMapperExactly()
    {
        var item = new MonitorEventItem(
            Id: "evt_deadbeefcafe1234",
            Ts: Ts,
            Type: "instance_crashed",
            Instance: "factorio-test",
            Actor: "system",
            Origin: "system",
            Data: Data(new { InstanceName = "factorio-test", ExitCode = "137", Restarts = "2" }));

        AuditRecord? shaped = MonitorEventShaping.Shape(item, HostId);
        Assert.NotNull(shaped);

        // The write-time equivalent: KgsmAuditConsumer would have built exactly this from the SAME
        // envelope fields, via AuditMapping.FromCrashEvent + ToRecordDirect (the id supplied by
        // EngineEventIdTracker at write time, here the monitor's own persisted id — same shape either way).
        var expectedData = new InstanceCrashedData
        {
            InstanceName = "factorio-test", ExitCode = "137", Restarts = "2",
            Timestamp = Ts, Actor = "system", Origin = "system",
        };
        AuditRecord expected = AuditMapping.ToRecordDirect(
            AuditMapping.FromCrashEvent(expectedData, HostId), item.Id);

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(shaped));
    }

    [Fact]
    public void Shape_ServerStarted_ProducesServerStartAction_WithProvenanceFromTheEnvelope()
    {
        var item = new MonitorEventItem(
            "evt_abc123", Ts, "instance_started", "mc", "discord:haru", "ui",
            Data(new { InstanceName = "mc" }));

        AuditRecord? shaped = MonitorEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.ServerStart, shaped!.Action);
        Assert.Equal("evt_abc123", shaped.Id);
        Assert.Equal(Ts, shaped.Ts);
        Assert.Equal("ui", shaped.Origin);
        Assert.Equal(ActorKind.User, shaped.Actor.Kind);
        Assert.Equal("haru", shaped.Actor.Name);
        Assert.Equal(ActorProvider.Discord, shaped.Actor.Provider);
        Assert.Equal("mc", shaped.ServerId);
        Assert.Equal(HostId, shaped.HostId);
    }

    // --- network.ports.open is dual-sourced (AuditQueries.EngineSourcedActions remarks) — the CLI-echo
    // half still shapes here from the monitor exactly like every other engine action. -----------------
    [Fact]
    public void Shape_PortsOpened_ShapesToNetworkPortsOpen()
    {
        var item = new MonitorEventItem(
            "evt_ports1", Ts, "instance_ports_opened", "mc", null, null,
            Data(new { InstanceName = "mc", Ports = Array.Empty<object>() }));

        AuditRecord? shaped = MonitorEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.NetworkPortsOpen, shaped!.Action);
    }

    // --- deliberately-silent types (transient sub-phase signals) never surface, matching write-time ---
    [Theory]
    [InlineData("instance_ready")]
    [InlineData("instance_installation_started")]
    [InlineData("instance_download_started")]
    [InlineData("instance_deploy_started")]
    public void Shape_SilentType_ReturnsNull(string type)
    {
        var item = new MonitorEventItem("evt_x", Ts, type, "mc", null, null, Data(new { InstanceName = "mc" }));
        Assert.Null(MonitorEventShaping.Shape(item, HostId));
    }

    // --- an unclassified event type is never dropped — an honest generic fallback, no fabricated field -
    [Fact]
    public void Shape_UnmappedType_GenericFallback_NeverDropsIt()
    {
        var item = new MonitorEventItem(
            "evt_unknown1", Ts, "instance_created", "mc", "discord:haru", "ui",
            Data(new { InstanceName = "mc", Blueprint = "factorio" }));

        AuditRecord? shaped = MonitorEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal("evt_unknown1", shaped!.Id);
        Assert.Equal("engine.instance_created", shaped.Action);
        Assert.Equal(AuditSeverity.Info, shaped.Severity);
        Assert.Equal("mc", shaped.ServerId);
        Assert.Equal("ui", shaped.Origin);
        Assert.Equal("haru", shaped.Actor.Name);
        Assert.NotNull(shaped.Target);
        Assert.Equal("mc", shaped.Target!.Id);
        Assert.Equal("instance_created", shaped.Meta!["eventType"]); // literal fact only, nothing fabricated
    }

    [Fact]
    public void Shape_UnmappedType_NoInstance_NoTarget_NullActorOrigin_NeverFabricated()
    {
        var item = new MonitorEventItem("evt_host1", Ts, "some_future_host_event", null, null, null, null);

        AuditRecord? shaped = MonitorEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Null(shaped!.Target);
        Assert.Null(shaped.ServerId);
        Assert.Null(shaped.Origin);
        Assert.Equal(ActorKind.System, shaped.Actor.Kind); // ParseActor's defensive fallback for no actor
    }

    // --- absent/malformed Data payload never throws — the mapper still gets a valid (blank) typed object
    [Fact]
    public void Shape_NullData_MappedType_DoesNotThrow_UsesEnvelopeInstance()
    {
        var item = new MonitorEventItem("evt_nodata", Ts, "instance_stopped", "mc", null, null, Data: null);

        AuditRecord? shaped = MonitorEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.ServerStop, shaped!.Action);
        Assert.Equal("mc", shaped.ServerId); // filled from item.Instance since Data carried none
    }
}
