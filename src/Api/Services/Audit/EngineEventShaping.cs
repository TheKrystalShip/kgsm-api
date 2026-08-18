using System.Globalization;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Leaves;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Lifecycle;

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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Whether an event is a step inside a larger operation rather than the news itself — the brackets
    /// kgsm puts around an install, a stop, an update. Those are live state, not history: the fact
    /// worth an append-only row is the one in the middle.
    /// </summary>
    /// <remarks>
    /// <b>The engine classifies its own events</b> (<see cref="KgsmEventCatalog"/>), so this holds no
    /// list of its own and a phase event added upstream is silent here the day the pin moves. The live
    /// path stays in step for a structural reason rather than a maintained one:
    /// <see cref="KgsmAuditConsumer"/> only publishes a row for a type it registers a mapping handler
    /// for, and the phase types it does register (the job-slot brackets) publish nothing by design —
    /// so the two halves of the merged feed show an operation the same way.
    /// </remarks>
    private static bool IsPhase(string type) =>
        KgsmEventCatalog.Describe(type).Weight == EventWeight.Phase;

    /// <summary>
    /// Whether <paramref name="type"/> is a leaf reporting on its own state.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Not an audit row.</b> The audit answers who did what, and nobody did these — a leaf coming
    /// up, losing a component or going away is a fact about a service rather than somebody's action,
    /// and its actor is the leaf itself. They already have a surface: the capability block reports them
    /// as <c>degraded</c> with the component named. Rendering them here would also mean every deploy
    /// writing a row per leaf into the record of what people did.
    /// <para>
    /// The generic fallback is deliberate everywhere else — an unclassified engine event must never be
    /// silently dropped — so this exclusion is by name rather than by falling through it.
    /// </para>
    /// </remarks>
    private static bool IsLeafLifecycle(string type) =>
        type is LeafLifecycleEvents.Ready
            or LeafLifecycleEvents.Degraded
            or LeafLifecycleEvents.Recovered
            or LeafLifecycleEvents.Stopping;

    /// <summary>
    /// Shape <paramref name="item"/> to its <see cref="AuditRecord"/>, or <see langword="null"/> for a
    /// step inside a larger operation (<see cref="IsPhase"/>) — the only case this returns null; every
    /// other event type is shaped, mapped when a <see cref="AuditMapping"/> mapper exists, else via an
    /// honest generic fallback (<see cref="GenericShape"/>) so a new/unclassified kgsm event is never
    /// silently dropped from the audit trail (Locked decision #8's whole point — a neutral raw store
    /// means an event the domain layer hasn't wired a mapper for yet still shows up).
    /// </summary>
    public static AuditRecord? Shape(EventHistoryEntry item, string hostId)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsPhase(item.Type)) return null;
        if (IsLeafLifecycle(item.Type)) return null;
        if (IsNoteAttributionChange(item)) return null;

        AuditWrite? write = item.Type switch
        {
            // -- what the assistant reports about ITSELF ---------------------------------------
            // ⚠ Not a record of what it did: every action it performs is already an engine event,
            // attributed to the person who asked. These are the turn that did NOT act, which leaves the
            // engine's record empty because from its side nothing happened.
            //
            // assistant_blueprint_authoring_started is absent on purpose — it is classified Phase, so
            // IsPhase already drops it, exactly like instance_installation_started. The bracket's open
            // half is not news; its close is.
            AssistantEvents.ActionDeclined => Map<AssistantActionDeclinedEventData>(item,
                d => Assistant(item, AuditAction.AssistantActionDeclined, AuditSeverity.Warn,
                    d.Instance is null
                        ? $"refused {d.Tool} — {Declined(d.DeclineReason)}"
                        : $"refused {d.Tool} on {d.Instance} — {Declined(d.DeclineReason)}",
                    hostId, d.Instance,
                    Meta(("tool", d.Tool), ("reason", d.DeclineReason), ("tier", d.Tier)))),

            AssistantEvents.ActionProposed => Map<AssistantActionProposedEventData>(item,
                d => Assistant(item, AuditAction.AssistantActionProposed, AuditSeverity.Info,
                    d.Instance is null
                        ? $"proposed {d.Kind}, awaiting approval"
                        : $"proposed {d.Kind} on {d.Instance}, awaiting approval",
                    hostId, d.Instance,
                    Meta(("kind", d.Kind), ("tool", d.Tool),
                         ("expiresInSec", d.ExpiresInSec?.ToString(CultureInfo.InvariantCulture))))),

            // No target: this is about the assistant's own honesty, not about anything on a server.
            AssistantEvents.ClaimCorrected => Map<AssistantClaimCorrectedEventData>(item,
                d => Assistant(item, AuditAction.AssistantClaimCorrected, AuditSeverity.Warn,
                    $"described work the turn did not do ({d.Check}) — {d.Resolution}",
                    hostId, instance: null,
                    Meta(("check", d.Check), ("resolution", d.Resolution), ("net", d.Net)))),

            AssistantEvents.BlueprintAuthored => Map<AssistantBlueprintAuthoredEventData>(item,
                d => new AuditWrite(
                    item.Ts, AuditMapping.NormalizeOrigin(item.Origin), AuditMapping.ParseActor(item.Actor),
                    AuditAction.AssistantBlueprintAuthored,
                    d.AuthoringOutcome == "verified" ? AuditSeverity.Success : AuditSeverity.Warn,
                    // Blueprint-targeted, and carrying no serverId: the probe was a disposable instance
                    // that no longer exists, and naming it as a server would put a row about a machine
                    // nobody has in the feed for one somebody does.
                    new AuditTarget(AuditTargetKind.Blueprint, d.BlueprintName, d.BlueprintName),
                    ServerId: null, hostId,
                    $"blueprint authoring for {d.BlueprintName} ended {d.AuthoringOutcome}",
                    Meta(("outcome", d.AuthoringOutcome), ("probe", d.Probe),
                         ("durationSec", d.DurationSec?.ToString(CultureInfo.InvariantCulture))))),

            "instance_started" => Map<InstanceStartedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerStart, AuditSeverity.Info, "started", hostId)),
            // The moment players can actually connect, which server.start does not report — that one
            // says the process spawned. Two facts about two different moments, and the second is the
            // one somebody asking "when could people get in" is looking for.
            "instance_ready" => Map<InstanceReadyData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerReady, AuditSeverity.Info, "finished loading", hostId)),
            "instance_stopped" => Map<InstanceStoppedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerStop, AuditSeverity.Info, "stopped", hostId)),
            "instance_restarted" => Map<InstanceRestartedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerRestart, AuditSeverity.Info, "restarted", hostId)),
            "instance_uninstalled" => Map<InstanceUninstalledData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerUninstall, AuditSeverity.Warn, "uninstalled", hostId)),
            "instance_version_updated" => Map<InstanceVersionUpdatedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerUpdate, AuditSeverity.Info, "updated", hostId,
                    Meta(("oldVersion", d.OldVersion), ("newVersion", d.NewVersion)))),
            // The update run that ended without the version moving, for a reason. Same action as the
            // successful one with the severity carrying the outcome — the server.crash shape, rather
            // than an invented action for every way a thing can fail.
            "instance_update_failed" => Map<InstanceUpdateFailedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerUpdate, AuditSeverity.Danger, "could not update", hostId)),
            "instance_uninstall_failed" => Map<InstanceUninstallFailedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerUninstall, AuditSeverity.Danger, "could not uninstall", hostId)),
            "instance_update_available" => Map<InstanceUpdateAvailableData>(item,
                d => AuditMapping.FromUpdateAvailableEvent(d, hostId)),
            "instance_installed" => Map<InstanceInstalledData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.ServerInstall, AuditSeverity.Success, "installed", hostId,
                    Meta(("blueprint", d.Blueprint)))),
            "instance_backup_created" => Map<InstanceBackupCreatedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupCreate, AuditSeverity.Success, "backed up", hostId,
                    Meta(("source", d.Source), ("version", d.Version)))),
            "instance_backup_restored" => Map<InstanceBackupRestoredData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupRestore, AuditSeverity.Success, "restored backup for", hostId,
                    Meta(("source", d.Source), ("version", d.Version)))),
            // Warn, not Success: destroying a backup is the one backup operation with no undo, and it
            // succeeding is exactly what makes it worth surfacing.
            "instance_backup_deleted" => Map<InstanceBackupDeletedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupDelete, AuditSeverity.Warn, "deleted a backup for", hostId,
                    Meta(("source", d.Source)))),
            "instance_backups_pruned" => Map<InstanceBackupsPrunedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupPrune, AuditSeverity.Info, "pruned backups for", hostId,
                    // `pinned` is what the sweep protected. Without it a sweep that removed nothing
                    // because everything was pinned reads exactly like one that found nothing to remove.
                    Meta(("deleted", d.Deleted.ToString(CultureInfo.InvariantCulture)),
                         ("kept", d.Kept.ToString(CultureInfo.InvariantCulture)),
                         ("pinned", d.Pinned.ToString(CultureInfo.InvariantCulture))))),
            "instance_backup_pinned" => Map<InstanceBackupPinnedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupPin, AuditSeverity.Info, "pinned a backup for", hostId,
                    Meta(("source", d.Source)))),
            // Warn, like a delete: unpinning is what lets the next sweep take an archive somebody
            // deliberately protected, and it succeeding is exactly what makes it worth surfacing.
            "instance_backup_unpinned" => Map<InstanceBackupUnpinnedData>(item,
                d => AuditMapping.FromServerEvent(d, AuditAction.BackupUnpin, AuditSeverity.Warn, "unpinned a backup for", hostId,
                    Meta(("source", d.Source)))),
            "instance_crashed" => Map<InstanceCrashedData>(item, d => AuditMapping.FromCrashEvent(d, hostId)),
            "instance_failed" => Map<InstanceFailedData>(item, d => AuditMapping.FromFailedEvent(d, hostId)),
            "instance_ports_opened" => Map<InstancePortsOpenedData>(item, d => AuditMapping.FromPortsOpenedEvent(d, hostId)),
            "instance_ports_closed" => Map<InstancePortsClosedData>(item, d => AuditMapping.FromPortsClosedEvent(d, hostId)),
            "instance_upnp_opened" => Map<InstanceUpnpOpenedData>(item, d => AuditMapping.FromUpnpOpenedEvent(d, hostId)),
            "instance_upnp_closed" => Map<InstanceUpnpClosedData>(item, d => AuditMapping.FromUpnpClosedEvent(d, hostId)),
            "instance_upnp_reasserted" => Map<InstanceUpnpReassertedData>(item, d => AuditMapping.FromUpnpReassertedEvent(d, hostId)),
            // kgsm-monitor's own journal: the host's measurements, shaped into rows here rather than
            // polled out of its database and transcribed.
            "host_threshold_breached" => Map<HostThresholdBreachedData>(item, d => AuditMapping.FromThresholdBreachedEvent(d, hostId)),
            "host_threshold_cleared" => Map<HostThresholdClearedData>(item, d => AuditMapping.FromThresholdClearedEvent(d, hostId)),

            // kgsm-api's own journal: what the Control Panel did itself. Shaped here like any other
            // producer's — nothing about these mappers knows the API is reading its own writing, which
            // is what keeps one code path serving the whole merged feed.
            ApiJournal.LoginEvent or ApiJournal.LogoutEvent or ApiJournal.ClusterSessionEvent =>
                Map<AuthSessionEventData>(item, d => AuditMapping.FromAuthSessionEvent(d, item.Type, hostId)),
            ApiJournal.SessionRevokedEvent =>
                Map<AuthSessionRevokedData>(item, d => AuditMapping.FromSessionRevokedEvent(d, hostId)),

            ApiJournal.UserProvisionedEvent or ApiJournal.UserApprovedEvent
                or ApiJournal.UserDisabledEvent or ApiJournal.UserTierChangedEvent
                or ApiJournal.UserDeletedEvent or ApiJournal.UserPasswordChangedEvent =>
                Map<UserAccountEventData>(item, d => AuditMapping.FromUserAccountEvent(d, item.Type, hostId)),

            ApiJournal.IdentityLinkedEvent or ApiJournal.IdentityUnlinkedEvent =>
                Map<IdentityLinkEventData>(item, d => AuditMapping.FromIdentityEvent(d, item.Type, hostId)),

            ApiJournal.ServiceConnectedEvent or ApiJournal.ServiceDisconnectedEvent =>
                Map<ServiceProvisioningEventData>(item,
                    d => AuditMapping.FromServiceProvisioningEvent(d, item.Type, hostId)),
            ApiJournal.ServiceConfigChangedEvent =>
                Map<ServiceConfigChangedEventData>(item, d => AuditMapping.FromServiceConfigEvent(d, hostId)),
            ApiJournal.ServiceRestartedEvent =>
                Map<ServiceRestartedEventData>(item, d => AuditMapping.FromServiceRestartedEvent(d, hostId)),

            ApiJournal.FileWrittenEvent =>
                Map<FileWrittenEventData>(item, d => AuditMapping.FromFileWrittenEvent(d, hostId)),
            ApiJournal.BackupDownloadedEvent =>
                Map<BackupDownloadedEventData>(item, d => AuditMapping.FromBackupDownloadedEvent(d, hostId)),
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
    // Constrained to the common root rather than the instance-scoped base: a host-scoped payload carries
    // the same envelope metadata and none of the instance identity, so the shared part is all this needs.
    private static AuditWrite? Map<T>(EventHistoryEntry item, Func<T, AuditWrite> build) where T : KgsmEventDataBase, new()
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
        // Instance-scoped payloads only: a host-scoped one has no instance to fall back to, and the
        // payload already carries the name in practice — this is the defensive path, not the normal one.
        if (typed is EventDataBase instanceScoped
            && string.IsNullOrEmpty(instanceScoped.InstanceName)
            && !string.IsNullOrEmpty(item.Instance))
        {
            instanceScoped.InstanceName = item.Instance;
        }

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
    /// <summary>
    /// One row about the assistant's own conduct.
    /// </summary>
    /// <remarks>
    /// Server-targeted when the refused or proposed action named an instance, and untargeted when it did
    /// not — a refusal to read a file and a corrected claim are about the assistant, not about a machine.
    /// ⚠ The actor and origin come off the envelope, which the leaf stamps from the same invocation the
    /// engine's own events carry, so a refusal and the action it refused name one person by one string.
    /// </remarks>
    private static AuditWrite Assistant(
        EventHistoryEntry item,
        string action,
        string severity,
        string summary,
        string hostId,
        string? instance,
        IReadOnlyDictionary<string, string>? meta) =>
        new(item.Ts, AuditMapping.NormalizeOrigin(item.Origin), AuditMapping.ParseActor(item.Actor),
            action, severity,
            instance is null ? null : new AuditTarget(AuditTargetKind.Server, instance, instance),
            instance, hostId, summary, meta);

    /// <summary>The refusal reason, in words a person reads.</summary>
    /// <remarks>
    /// ⚠ The two are very different facts and the row must not blur them: one host has actions switched
    /// off for everybody, the other has a person reaching past their tier. An unrecognised value is
    /// restated rather than guessed at.
    /// </remarks>
    private static string Declined(string reason) => reason switch
    {
        AssistantDeclineReasons.Authority => "their tier does not carry it",
        AssistantDeclineReasons.ActionsDisabled => "this host has actions turned off",
        _ => reason,
    };

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
