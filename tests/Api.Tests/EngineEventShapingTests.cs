using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// <see cref="EngineEventShaping"/> — the read-time domain shaping: turning one
/// of kgsm-monitor's raw, neutral <c>GET /events</c> rows into the exact same <see cref="AuditRecord"/>
/// shape <see cref="TheKrystalShip.Api.Services.Audit.KgsmAuditConsumer"/> used to persist at write time,
/// by reusing the very same <see cref="AuditMapping"/> <c>From*Event</c> mappers.
/// </summary>
public sealed class EngineEventShapingTests
{
    private const string HostId = "h1";
    private static readonly DateTimeOffset Ts = new(2026, 7, 18, 10, 30, 0, TimeSpan.Zero);

    private static JsonElement Data(object o) => JsonSerializer.SerializeToElement(o);

    // --- Fidelity: a mapped type shapes to BYTE-IDENTICAL JSON as the write-time mapper would have ----
    [Fact]
    public void Shape_MappedType_MatchesTheWriteTimeMapperExactly()
    {
        var item = new EventHistoryEntry(
            Id: "evt_deadbeefcafe1234",
            Ts: Ts,
            Type: "instance_crashed",
            Instance: "factorio-test",
            Blueprint: null,
            Actor: "system",
            Origin: "system",
            Hostname: null,
            Data: Data(new { InstanceName = "factorio-test", ExitCode = "137", Restarts = "2" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);
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
        var item = new EventHistoryEntry(
            "evt_abc123", Ts, "instance_started", "mc", null, "discord:haru", "ui", null,
            Data(new { InstanceName = "mc" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

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
        var item = new EventHistoryEntry(
            "evt_ports1", Ts, "instance_ports_opened", "mc", null, null, null, null,
            Data(new { InstanceName = "mc", Ports = Array.Empty<object>() }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.NetworkPortsOpen, shaped!.Action);
    }

    // --- deliberately-silent types (transient sub-phase signals) never surface, matching write-time ---
    [Theory]
    [InlineData("instance_ready")]
    [InlineData("instance_installation_started")]
    [InlineData("instance_download_started")]
    [InlineData("instance_deploy_started")]
    [InlineData("instance_update_started")]
    [InlineData("instance_update_finished")]
    [InlineData("instance_stop_started")]
    [InlineData("instance_stop_finished")]
    [InlineData("instance_restart_started")]
    [InlineData("instance_restart_finished")]
    public void Shape_SilentType_ReturnsNull(string type)
    {
        var item = new EventHistoryEntry("evt_x", Ts, type, "mc", null, null, null, null, Data(new { InstanceName = "mc" }));
        Assert.Null(EngineEventShaping.Shape(item, HostId));
    }

    // --- an unclassified event type is never dropped — an honest generic fallback, no fabricated field -
    [Fact]
    public void Shape_UnmappedType_GenericFallback_NeverDropsIt()
    {
        var item = new EventHistoryEntry(
            "evt_unknown1", Ts, "instance_created", "mc", null, "discord:haru", "ui", null,
            Data(new { InstanceName = "mc", Blueprint = "factorio" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

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
        var item = new EventHistoryEntry("evt_host1", Ts, "some_future_host_event", null, null, null, null, null, null);

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

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
        var item = new EventHistoryEntry("evt_nodata", Ts, "instance_stopped", "mc", null, null, null, null, Data: null);

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.ServerStop, shaped!.Action);
        Assert.Equal("mc", shaped.ServerId); // filled from item.Instance since Data carried none
    }
}

/// <summary>
/// The blueprint events' read-time shaping — the first engine events whose subject is a blueprint rather
/// than an instance. They take a different path through <see cref="EngineEventShaping"/> than every other
/// type (their data derives from the sibling <c>BlueprintEventDataBase</c>, not <c>EventDataBase</c>), so
/// the two properties worth pinning are that they shape at all, and that they never acquire a server.
/// </summary>
public sealed class BlueprintEventShapingTests
{
    private const string HostId = "h1";
    private static readonly DateTimeOffset Ts = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Data(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Created_ShapesToABlueprintWriteRow_TargetingTheBlueprint()
    {
        var item = new EventHistoryEntry(
            Id: "evt_bp1", Ts: Ts, Type: "blueprint_created", Instance: null,
            Blueprint: "factorio",
            Actor: "discord:haru", Origin: "ui", Hostname: null,
            Data: Data(new { BlueprintName = "factorio", Tier = "user", OverridesSystem = true, Runtime = "native" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.BlueprintWrite, shaped!.Action);
        Assert.Equal(AuditTargetKind.Blueprint, shaped.Target!.Kind);
        Assert.Equal("factorio", shaped.Target.Id);
        // A blueprint is the TEMPLATE servers are installed from, not a server — so a serverId here would
        // make `GET /audit?serverId=factorio` return an edit that never touched any instance.
        Assert.Null(shaped.ServerId);
        Assert.Equal("haru", shaped.Actor.Name);   // the real admin, not the service account
        Assert.Equal("ui", shaped.Origin);
        Assert.Equal("user", shaped.Meta!["tier"]);
        Assert.Equal("true", shaped.Meta["overridesSystem"]);
        Assert.Equal("native", shaped.Meta["runtime"]);
        Assert.Contains("overrode", shaped.Summary); // shadowing a shipped blueprint, not merely creating one
    }

    [Fact]
    public void Updated_IsTheSameActionAtInfo()
    {
        var item = new EventHistoryEntry(
            Id: "evt_bp2", Ts: Ts, Type: "blueprint_updated", Instance: null, Blueprint: "palworld", Actor: "discord:haru", Origin: "ui", Hostname: null,
            Data: Data(new { BlueprintName = "palworld", Tier = "user", OverridesSystem = false, Runtime = "native" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.Equal(AuditAction.BlueprintWrite, shaped!.Action);
        Assert.Equal(AuditSeverity.Info, shaped.Severity);
        Assert.Equal("false", shaped.Meta!["overridesSystem"]);
    }

    [Fact]
    public void Removed_IsARevertWhenTheShippedBlueprintTookOver()
    {
        var item = new EventHistoryEntry(
            Id: "evt_bp3", Ts: Ts, Type: "blueprint_removed", Instance: null, Blueprint: "palworld", Actor: "discord:haru", Origin: "ui", Hostname: null,
            Data: Data(new { BlueprintName = "palworld", Tier = "user", RevertedToSystem = true }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.Equal(AuditAction.BlueprintRevert, shaped!.Action);
        Assert.Equal(AuditSeverity.Warn, shaped.Severity);
        Assert.Contains("reverted", shaped.Summary);
        Assert.Equal("true", shaped.Meta!["revertedToSystem"]);
    }

    [Fact]
    public void Removed_WithNoShippedOriginal_SaysRemovedNotReverted()
    {
        var item = new EventHistoryEntry(
            Id: "evt_bp4", Ts: Ts, Type: "blueprint_removed", Instance: null, Blueprint: "teamfortress2", Actor: "discord:haru", Origin: "ui", Hostname: null,
            Data: Data(new { BlueprintName = "teamfortress2", Tier = "user", RevertedToSystem = false }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        // The blueprint left this host entirely — a materially different fact from falling back to shipped.
        Assert.Contains("removed", shaped!.Summary);
        Assert.DoesNotContain("reverted", shaped.Summary);
    }

    [Fact]
    public void UnknownOverrideState_IsOmittedFromMeta_NeverCollapsedToFalse()
    {
        // The emitter could not determine whether this shadows a shipped blueprint. "Unknown" is not "no",
        // so the key is absent rather than answered.
        var item = new EventHistoryEntry(
            Id: "evt_bp5", Ts: Ts, Type: "blueprint_updated", Instance: null, Blueprint: "factorio", Actor: "discord:haru", Origin: "ui", Hostname: null,
            Data: Data(new { BlueprintName = "factorio", Tier = "user" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.False(shaped!.Meta!.ContainsKey("overridesSystem"));
        Assert.False(shaped.Meta.ContainsKey("runtime"));
    }
}
