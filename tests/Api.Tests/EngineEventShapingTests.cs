using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

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

    // --- the two backup-removal events shape to their own actions, never to one shared "removed" ------
    [Fact]
    public void Shape_BackupDeleted_ShapesToBackupDelete_CarryingTheBackupId()
    {
        var item = new EventHistoryEntry(
            "evt_del1", Ts, "instance_backup_deleted", "mc", null, "discord:haru", "ui", null,
            Data(new { InstanceName = "mc", Source = "mc-20260731T142233Z-a3f9c1" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.BackupDelete, shaped!.Action);
        // Warn, not Success: destroying a backup is the one backup operation with no undo.
        Assert.Equal(AuditSeverity.Warn, shaped.Severity);
        Assert.Equal("mc-20260731T142233Z-a3f9c1", shaped.Meta!["source"]);
        Assert.Equal("mc", shaped.ServerId);
    }

    // The read-back half of the update-available echo. Both paths that turn this event into a row — the
    // live push and this one — have to name the same action, or the row a client saw over SSE and the row
    // it finds in GET /audit are two different facts. Shaping it generically (engine.instance_*) is what
    // that looks like when only one of the two is wired.
    [Fact]
    public void Shape_UpdateAvailable_ShapesToServerUpdateAvailable_CarryingBothVersions()
    {
        var item = new EventHistoryEntry(
            "evt_upd1", Ts, "instance_update_available", "starbound", null, "system:scheduler", "system", null,
            Data(new { InstanceName = "starbound", CurrentVersion = "16000000", LatestVersion = "16302742" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.ServerUpdateAvailable, shaped!.Action);
        Assert.Equal(AuditSeverity.Info, shaped.Severity);
        Assert.Equal("update available for starbound", shaped.Summary);
        Assert.Equal("16000000", shaped.Meta!["currentVersion"]);
        Assert.Equal("16302742", shaped.Meta!["latestVersion"]);
        Assert.Equal("starbound", shaped.ServerId);
    }

    [Fact]
    public void Shape_BackupsPruned_ShapesToBackupPrune_CarryingTheCounts()
    {
        var item = new EventHistoryEntry(
            "evt_prune1", Ts, "instance_backups_pruned", "mc", null, "system:scheduler", "system", null,
            Data(new { InstanceName = "mc", Deleted = 3, Kept = 5 }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.BackupPrune, shaped!.Action);
        // Info, not Warn: retention policy running to plan is the healthy case. The delete above is
        // the one an operator should notice.
        Assert.Equal(AuditSeverity.Info, shaped.Severity);
        Assert.Equal("3", shaped.Meta!["deleted"]);
        Assert.Equal("5", shaped.Meta["kept"]);
        // A prune is a sweep — it names no single backup, so nothing may claim one.
        Assert.False(shaped.Meta.ContainsKey("source"));
    }

    // --- a step inside a larger operation never surfaces; the engine decides which those are --------
    [Theory]
    [InlineData("instance_installation_started")]
    [InlineData("instance_download_started")]
    [InlineData("instance_deploy_started")]
    [InlineData("instance_update_started")]
    [InlineData("instance_update_finished")]
    [InlineData("instance_stop_started")]
    [InlineData("instance_stop_finished")]
    [InlineData("instance_restart_started")]
    [InlineData("instance_restart_finished")]
    // The install brackets, which the local skip-list never named and the generic fallback shaped into
    // rows nobody wanted: an install produced a dozen "engine.instance_files_created" lines beside it.
    [InlineData("instance_created")]
    [InlineData("instance_files_created")]
    [InlineData("instance_directories_created")]
    [InlineData("instance_downloaded")]
    [InlineData("instance_deployed")]
    public void Shape_AStepInsideAnOperation_ReturnsNull(string type)
    {
        var item = new EventHistoryEntry("evt_x", Ts, type, "mc", null, null, null, null, Data(new { InstanceName = "mc" }));
        Assert.Null(EngineEventShaping.Shape(item, HostId));
    }

    /// <summary>
    /// The silence is the engine's classification rather than a list kept here, so every type it calls
    /// a phase is silent — including ones added upstream after this test was written.
    /// </summary>
    [Fact]
    public void Shape_EveryTypeTheEngineCallsAPhase_IsSilent()
    {
        foreach (EventDescriptor descriptor in KgsmEventCatalog.All.Where(d => d.Weight == EventWeight.Phase))
        {
            var item = new EventHistoryEntry(
                "evt_x", Ts, descriptor.Type, "mc", null, null, null, null, Data(new { InstanceName = "mc" }));

            Assert.Null(EngineEventShaping.Shape(item, HostId));
        }
    }

    /// <summary>
    /// <b>The other direction, and the one that keeps a hand-written skip-list from growing back.</b>
    /// Silence is the engine's classification, so an event it calls a fact reaches the trail — mapped
    /// where a mapper exists, generically where none does, but never dropped. A type silenced by name
    /// here would fail this the moment it was added, which is what the ten-entry list it replaced
    /// could not do.
    /// </summary>
    [Fact]
    public void Shape_EveryFactTheEngineReports_BecomesARow()
    {
        List<string> dropped = [];

        foreach (EventDescriptor descriptor in KgsmEventCatalog.All.Where(d => d.Weight == EventWeight.Fact))
        {
            // ⚠ The one exception, and it is by name so adding another is a deliberate act. A leaf
            // reporting on its own state is a fact about a service, not somebody's action, and the
            // audit answers who did what. They have their own surface: the capability block reports
            // them as degraded with the component named. Rendering them here would also mean every
            // deploy writing a row per leaf into the record of what people did.
            if (LeafLifecycle.Contains(descriptor.Type))
                continue;

            var item = new EventHistoryEntry(
                "evt_x", Ts, descriptor.Type, "mc", null, null, null, null, Data(new { InstanceName = "mc" }));

            if (EngineEventShaping.Shape(item, HostId) is null)
                dropped.Add(descriptor.Type);
        }

        Assert.True(dropped.Count == 0,
            "these events report a fact and produce no audit row: " + string.Join(", ", dropped));
    }

    /// <summary>The four events excluded above, and the assertion that they really are excluded.</summary>
    private static readonly string[] LeafLifecycle =
    [
        LeafLifecycleEvents.Ready,
        LeafLifecycleEvents.Degraded,
        LeafLifecycleEvents.Recovered,
        LeafLifecycleEvents.Stopping,
    ];

    [Fact]
    public void Shape_ALeafReportingOnItself_IsNotAnAuditRow()
    {
        // The other direction of the exclusion above: skipping them in the coverage test would pass
        // just as well if they quietly started producing rows.
        foreach (string type in LeafLifecycle)
        {
            var item = new EventHistoryEntry(
                "evt_x", Ts, type, null, null, null, null, null, Data(new { Component = "backend" }));

            Assert.Null(EngineEventShaping.Shape(item, HostId));
        }
    }

    // --- what the assistant reports about itself -------------------------------------------------

    /// <summary>
    /// A refusal for want of authority becomes a row, because it exists nowhere else.
    /// </summary>
    /// <remarks>
    /// ⚠ The engine cannot hold this: nothing ran, so from its side nothing happened. Warn rather than
    /// info — nothing broke, and somebody reached for something they could not do.
    /// </remarks>
    [Fact]
    public void Shape_AssistantActionDeclined_IsAWarnRowNamingTheTool()
    {
        var item = new EventHistoryEntry(
            "evt_dec", Ts, AssistantEvents.ActionDeclined, null, null, "discord:haru", "assistant", null,
            Data(new { Tool = "server_command", DeclineReason = "authority", Tier = "viewer", Instance = "mc" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.AssistantActionDeclined, shaped!.Action);
        Assert.Equal(AuditSeverity.Warn, shaped.Severity);
        Assert.Equal("mc", shaped.ServerId);
        Assert.Contains("server_command", shaped.Summary);
        Assert.Contains("tier does not carry it", shaped.Summary);
    }

    /// <summary>
    /// ⚠ The two refusal reasons read differently, because they are different facts.
    /// </summary>
    /// <remarks>
    /// A host with actions switched off refuses everybody, which is a configuration state; a host with
    /// them on refuses the person. Reading the first as the second turns a permanent setting into a
    /// stream of apparent overreach.
    /// </remarks>
    [Fact]
    public void Shape_AssistantActionDeclined_DistinguishesAConfigStateFromOverreach()
    {
        var item = new EventHistoryEntry(
            "evt_dec2", Ts, AssistantEvents.ActionDeclined, null, null, "discord:haru", "assistant", null,
            Data(new { Tool = "server_command", DeclineReason = "actions_disabled" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Contains("host has actions turned off", shaped!.Summary);
        Assert.DoesNotContain("tier", shaped.Summary);
    }

    /// <summary>A staged action awaiting a person becomes a row; nothing has run, so the engine has none.</summary>
    [Fact]
    public void Shape_AssistantActionProposed_IsAnInfoRow()
    {
        var item = new EventHistoryEntry(
            "evt_prop", Ts, AssistantEvents.ActionProposed, null, null, "discord:haru", "assistant", null,
            Data(new { Kind = "Backup", Instance = "mc", ExpiresInSec = 300 }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.AssistantActionProposed, shaped!.Action);
        Assert.Equal(AuditSeverity.Info, shaped.Severity);
        Assert.Contains("awaiting approval", shaped.Summary);
        Assert.Equal("300", shaped.Meta!["expiresInSec"]);
    }

    /// <summary>
    /// A corrected claim is about the assistant's honesty, so it targets nothing.
    /// </summary>
    /// <remarks>
    /// ⚠ Naming a server here would file the model's own fabrication as something that happened to a
    /// machine, and it would surface on that server's timeline as though it had.
    /// </remarks>
    [Fact]
    public void Shape_AssistantClaimCorrected_CarriesNoTarget()
    {
        var item = new EventHistoryEntry(
            "evt_claim", Ts, AssistantEvents.ClaimCorrected, null, null, "discord:haru", "assistant", null,
            Data(new { Check = "unbacked_action", Resolution = "corrected", Net = "outer", ConversationId = "web:u:1" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.AssistantClaimCorrected, shaped!.Action);
        Assert.Null(shaped.Target);
        Assert.Null(shaped.ServerId);
        Assert.Equal("outer", shaped.Meta!["net"]);
    }

    /// <summary>
    /// An authoring run's close is blueprint-targeted and names no server.
    /// </summary>
    /// <remarks>
    /// ⚠ The probe was a disposable instance that no longer exists. Carrying it as a serverId would put
    /// a row about a machine nobody has into the feed for one somebody does; it rides in meta instead,
    /// which is what ties this row to the twenty-odd install and uninstall rows the engine wrote.
    /// </remarks>
    [Fact]
    public void Shape_AssistantBlueprintAuthored_TargetsTheBlueprintAndNotTheProbe()
    {
        var item = new EventHistoryEntry(
            "evt_auth", Ts, AssistantEvents.BlueprintAuthored, null, null, "discord:haru", "assistant", null,
            Data(new
            {
                BlueprintName = "terraria",
                Probe = "__bp_probe_terraria__",
                AuthoringOutcome = "verified",
                DurationSec = 214,
            }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.AssistantBlueprintAuthored, shaped!.Action);
        Assert.Equal(AuditSeverity.Success, shaped.Severity);
        Assert.Equal(AuditTargetKind.Blueprint, shaped.Target!.Kind);
        Assert.Equal("terraria", shaped.Target.Id);
        Assert.Null(shaped.ServerId);
        Assert.Equal("__bp_probe_terraria__", shaped.Meta!["probe"]);
    }

    /// <summary>
    /// ⚠ The bracket's opening half produces no row.
    /// </summary>
    /// <remarks>
    /// It is classified <c>Phase</c>, exactly like <c>instance_installation_started</c>: a step inside a
    /// larger operation that has its own fact event. A feed showing both would report every authoring
    /// run twice.
    /// </remarks>
    [Fact]
    public void Shape_AssistantBlueprintAuthoringStarted_IsAPhaseAndNotARow()
    {
        var item = new EventHistoryEntry(
            "evt_start", Ts, AssistantEvents.BlueprintAuthoringStarted, null, null, "discord:haru", "assistant", null,
            Data(new { BlueprintName = "terraria", Probe = "__bp_probe_terraria__" }));

        Assert.Null(EngineEventShaping.Shape(item, HostId));
    }

    /// <summary>
    /// <c>instance_ready</c> is a fact, not a refinement of <c>server.start</c>: that one says the
    /// process spawned, this one says the game will accept a connection, and on a big world the gap is
    /// minutes. It gets its own action rather than being folded into the start it followed.
    /// </summary>
    [Fact]
    public void Shape_Ready_IsItsOwnFactAndNotSilent()
    {
        var item = new EventHistoryEntry(
            "evt_ready", Ts, "instance_ready", "mc", null, null, null, null, Data(new { InstanceName = "mc" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal(AuditAction.ServerReady, shaped!.Action);
        Assert.NotEqual(AuditAction.ServerStart, shaped.Action);
        Assert.Equal("mc", shaped.ServerId);
    }

    // --- an unclassified event type is never dropped — an honest generic fallback, no fabricated field -
    [Fact]
    public void Shape_UnmappedType_GenericFallback_NeverDropsIt()
    {
        var item = new EventHistoryEntry(
            "evt_unknown1", Ts, "instance_some_future_thing", "mc", null, "discord:haru", "ui", null,
            Data(new { InstanceName = "mc", Blueprint = "factorio" }));

        AuditRecord? shaped = EngineEventShaping.Shape(item, HostId);

        Assert.NotNull(shaped);
        Assert.Equal("evt_unknown1", shaped!.Id);
        Assert.Equal("engine.instance_some_future_thing", shaped.Action);
        Assert.Equal(AuditSeverity.Info, shaped.Severity);
        Assert.Equal("mc", shaped.ServerId);
        Assert.Equal("ui", shaped.Origin);
        Assert.Equal("haru", shaped.Actor.Name);
        Assert.NotNull(shaped.Target);
        Assert.Equal("mc", shaped.Target!.Id);
        // literal fact only, nothing fabricated
        Assert.Equal("instance_some_future_thing", shaped.Meta!["eventType"]);
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
