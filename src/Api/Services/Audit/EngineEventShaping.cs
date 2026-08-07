using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// Shapes one raw <see cref="EventHistoryEntry"/> — an engine envelope as the journal holds it, neutral
/// and undomained — into an <see cref="AuditRecord"/>, through the same <see cref="AuditMapping"/>
/// <c>From*Event</c> mappers the live path uses. That shared mapping is what makes a row read back from
/// history indistinguishable from the one pushed live for the same event.
/// <para>
/// The split is deliberate: the journal stores what happened, and the domain vocabulary — dotted
/// actions, severity, a human summary — is applied here, at read time. Storing the shaped form would
/// freeze one consumer's vocabulary into the record, and every other consumer would have to live with
/// it.
/// </para>
/// </summary>
public static class EngineEventShaping
{
    // Event types KgsmAuditConsumer deliberately never audits — transient
    // sub-phase/refinement signals, not new domain facts (see the consumer's own remarks on
    // InstanceReadyData / the PublishPhase handlers). Kept invisible in the merged feed too, so the
    // shaped output matches write-time behavior exactly rather than surfacing previously-silent noise.
    private static readonly HashSet<string> SilentTypes = new(StringComparer.Ordinal)
    {
        "instance_ready",                    // refines server.start's run-state; not a new fact
        "instance_installation_started",     // job.patch phase signal only
        "instance_download_started",         // job.patch phase signal only
        "instance_deploy_started",           // job.patch phase signal only
        "instance_update_started",           // claims the in-flight job slot; server.update is the fact
        "instance_update_finished",          // releases it — "the run ended", not "the version moved"
        "instance_stop_started",             // same bracket for a shutdown; server.stop is the fact
        "instance_stop_finished",            // releases it — "the run ended", not "the instance is down"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Shape <paramref name="item"/> to its <see cref="AuditRecord"/>, or <see langword="null"/> for a
    /// deliberately-silent type (<see cref="SilentTypes"/>) — the only case this returns null; every
    /// other event type is shaped, mapped when a <see cref="AuditMapping"/> mapper exists, else via an
    /// honest generic fallback (<see cref="GenericShape"/>) so a new/unclassified kgsm event is never
    /// silently dropped from the audit trail (Locked decision #8's whole point — a neutral raw store
    /// means an event the domain layer hasn't wired a mapper for yet still shows up).
    /// </summary>
    public static AuditRecord? Shape(EventHistoryEntry item, string hostId)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (SilentTypes.Contains(item.Type)) return null;
        if (IsNoteAttributionChange(item)) return null;

        AuditWrite? write = item.Type switch
        {
            "instance_started" => Map<InstanceStartedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerStart, AuditSeverity.Info, "started", hostId)),
            "instance_stopped" => Map<InstanceStoppedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerStop, AuditSeverity.Info, "stopped", hostId)),
            "instance_restarted" => Map<InstanceRestartedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerRestart, AuditSeverity.Info, "restarted", hostId)),
            "instance_uninstalled" => Map<InstanceUninstalledData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerUninstall, AuditSeverity.Warn, "uninstalled", hostId)),
            "instance_version_updated" => Map<InstanceVersionUpdatedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerUpdate, AuditSeverity.Info, "updated", hostId,
                    Meta(("oldVersion", d.OldVersion), ("newVersion", d.NewVersion)))),
            "instance_installed" => Map<InstanceInstalledData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerInstall, AuditSeverity.Success, "installed", hostId,
                    Meta(("blueprint", d.Blueprint)))),
            "instance_backup_created" => Map<InstanceBackupCreatedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupCreate, AuditSeverity.Success, "backed up", hostId,
                    Meta(("source", d.Source), ("version", d.Version)))),
            "instance_backup_restored" => Map<InstanceBackupRestoredData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupRestore, AuditSeverity.Success, "restored backup for", hostId,
                    Meta(("source", d.Source), ("version", d.Version)))),
            "instance_crashed" => Map<InstanceCrashedData>(item, d => AuditMapping.FromCrashEvent(d, hostId)),
            "instance_failed" => Map<InstanceFailedData>(item, d => AuditMapping.FromFailedEvent(d, hostId)),
            // network.ports.open is dual-sourced (see AuditQueries.EngineSourcedActions remarks) — the
            // CLI-echo half is shaped here from the journal exactly like every other engine action; the
            // api-issued open_ports command's DIRECT local write is untouched and disjoint.
            "instance_ports_opened" => Map<InstancePortsOpenedData>(item, d => AuditMapping.FromPortsOpenedEvent(d, hostId)),
            "instance_ports_closed" => Map<InstancePortsClosedData>(item, d => AuditMapping.FromPortsClosedEvent(d, hostId)),
            "instance_upnp_opened" => Map<InstanceUpnpOpenedData>(item, d => AuditMapping.FromUpnpOpenedEvent(d, hostId)),
            "instance_upnp_closed" => Map<InstanceUpnpClosedData>(item, d => AuditMapping.FromUpnpClosedEvent(d, hostId)),
            "instance_player_joined" => Map<InstancePlayerJoinedData>(item, d => AuditMapping.FromPlayerJoinedEvent(d, hostId)),
            "instance_player_left" => Map<InstancePlayerLeftData>(item, d => AuditMapping.FromPlayerLeftEvent(d, hostId)),
            "instance_player_kicked" => Map<InstancePlayerKickedData>(item,
                d => AuditMapping.FromPlayerModerationEvent(d, hostId, AuditAction.PlayerKick, "kicked")),
            "instance_player_banned" => Map<InstancePlayerBannedData>(item,
                d => AuditMapping.FromPlayerModerationEvent(d, hostId, AuditAction.PlayerBan, "banned")),
            "instance_player_unbanned" => Map<InstancePlayerUnbannedData>(item,
                d => AuditMapping.FromPlayerModerationEvent(d, hostId, AuditAction.PlayerUnban, "unbanned")),
            "instance_config_changed" => Map<InstanceConfigChangedData>(item, d => AuditMapping.FromConfigChangedEvent(d, hostId)),
            "instance_input_sent" => Map<InstanceInputSentData>(item, d => AuditMapping.FromInputSentEvent(d, hostId)),
            // The blueprint events are the first whose subject is NOT an instance, so they go through
            // MapBlueprint rather than Map — see that helper for why the two cannot share one.
            "blueprint_created" => MapBlueprint<BlueprintCreatedData>(item,
                d => AuditMapping.FromBlueprintCreatedEvent(d, hostId)),
            "blueprint_updated" => MapBlueprint<BlueprintUpdatedData>(item,
                d => AuditMapping.FromBlueprintUpdatedEvent(d, hostId)),
            "blueprint_removed" => MapBlueprint<BlueprintRemovedData>(item,
                d => AuditMapping.FromBlueprintRemovedEvent(d, hostId)),
            _ => null,
        };

        return write is not null ? AuditMapping.ToRecordDirect(write, item.Id) : GenericShape(item, hostId);
    }

    // A server note is one operator action spread over three config keys (body + who + when), so the
    // engine emits three instance_config_changed events for it. Only the body's event is surfaced; the
    // two attribution keys are dropped here so an edit reads as one line in a feed that shows three.
    // Nothing is destroyed — the raw events remain in the journal, which is the record.
    // The live path (KgsmAuditConsumer) applies the same rule, so both halves of the merge agree.
    private static bool IsNoteAttributionChange(EventHistoryEntry item)
    {
        if (!string.Equals(item.Type, "instance_config_changed", StringComparison.Ordinal)) return false;
        if (item.Data is not { ValueKind: JsonValueKind.Object } data) return false;

        // kgsm emits the payload PascalCased ("Key"); JsonElement lookup is case-sensitive, so accept
        // either spelling rather than depending on the engine's casing staying put.
        if (!data.TryGetProperty("Key", out JsonElement key) && !data.TryGetProperty("key", out key))
            return false;

        return key.ValueKind == JsonValueKind.String
            && AuditMapping.IsNoteAttributionKey(key.GetString());
    }

    // Deserialize item.Data into T (reflection-based STJ — fine under kgsm-api's JIT runtime, unlike
    // kgsm-lib's own AOT-constrained source-gen path), then stamp the envelope-level fields EventService
    // normally copies onto a typed handler's data (Timestamp/Actor/Origin live on the WRAPPER, not inside
    // Data — see EventDataBase's remarks) before handing off to the real From*Event mapper. Null/failed
    // deserialize falls back to a blank T (never throws) so a malformed or absent Data payload still
    // yields an honest row via the mapper's own null-handling, rather than vanishing.
    private static AuditWrite? Map<T>(EventHistoryEntry item, Func<T, AuditWrite> build) where T : EventDataBase, new()
    {
        T typed;
        if (item.Data is { ValueKind: JsonValueKind.Object } data)
        {
            try { typed = data.Deserialize<T>(JsonOptions) ?? new T(); }
            catch (JsonException) { return null; }
        }
        else
        {
            typed = new T();
        }

        typed.Timestamp = item.Ts;
        typed.Actor = item.Actor;
        typed.Origin = item.Origin;
        if (string.IsNullOrEmpty(typed.InstanceName) && !string.IsNullOrEmpty(item.Instance))
            typed.InstanceName = item.Instance; // defensive — Data already carries it in practice

        return build(typed);
    }

    // The blueprint-subject counterpart of Map. It cannot share that helper: Map is generic over
    // EventDataBase (whose defining field is InstanceName) while these derive from the sibling
    // BlueprintEventDataBase, and the two meet only at the subject-neutral root. The instance-name
    // backfill is likewise absent by design — an envelope's `instance` field is empty for a blueprint
    // event, and copying it anywhere here would invent a server relationship that does not exist.
    private static AuditWrite? MapBlueprint<T>(EventHistoryEntry item, Func<T, AuditWrite> build)
        where T : BlueprintEventDataBase, new()
    {
        T typed;
        if (item.Data is { ValueKind: JsonValueKind.Object } data)
        {
            try { typed = data.Deserialize<T>(JsonOptions) ?? new T(); }
            catch (JsonException) { return null; }
        }
        else
        {
            typed = new T();
        }

        typed.Timestamp = item.Ts;
        typed.Actor = item.Actor;
        typed.Origin = item.Origin;

        return build(typed);
    }

    // A genuinely unclassified event type — never drop it. No fabricated detail: actor/origin/target are
    // exactly what the envelope carried (or null), and meta records only the literal raw type name.
    private static AuditRecord GenericShape(EventHistoryEntry item, string hostId)
    {
        AuditActor actor = AuditMapping.ParseActor(item.Actor);
        string? origin = AuditMapping.NormalizeOrigin(item.Origin);
        AuditTarget? target = string.IsNullOrEmpty(item.Instance)
            ? null
            : new AuditTarget(AuditTargetKind.Server, item.Instance, item.Instance);

        return new AuditRecord(
            item.Id, item.Ts, origin, actor,
            $"engine.{item.Type}", AuditSeverity.Info, target,
            item.Instance, hostId,
            string.IsNullOrEmpty(item.Instance) ? item.Type : $"{item.Type} — {item.Instance}",
            new Dictionary<string, string> { ["eventType"] = item.Type });
    }

    // Build a meta dict from non-empty pairs (a blank value is omitted, never stored as ""). Null if
    // empty. A small duplicate of KgsmAuditConsumer's private helper of the same shape — kept local so
    // this read-time shaping stays a self-contained, independently unit-testable file.
    private static IReadOnlyDictionary<string, string>? Meta(params (string Key, string? Value)[] pairs)
    {
        Dictionary<string, string>? meta = null;
        foreach ((string key, string? value) in pairs)
        {
            if (string.IsNullOrEmpty(value)) continue;
            meta ??= new Dictionary<string, string>(pairs.Length);
            meta[key] = value;
        }
        return meta;
    }
}
