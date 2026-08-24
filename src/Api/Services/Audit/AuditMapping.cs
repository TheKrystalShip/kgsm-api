using System.Globalization;
using TheKrystalShip.Api.Services.Alerts;
using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Data;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Services.Audit;

/// <summary>
/// An audit row to append — the internal input to <see cref="AuditService.AppendAsync"/>. Carries the
/// already-resolved provenance (<see cref="Actor"/>/<see cref="Origin"/>) and the mapped action; the
/// service assigns the public id, persists, and pushes the <c>audit.append</c> frame.
/// </summary>
public sealed record AuditWrite(
    DateTimeOffset Ts,
    string? Origin,
    AuditActor Actor,
    string Action,
    string Severity,
    AuditTarget? Target,
    string? ServerId,
    string? HostId,
    string Summary,
    IReadOnlyDictionary<string, string>? Meta);

/// <summary>
/// Pure mapping between the audit wire DTO (<see cref="AuditRecord"/>), the EF row
/// (<see cref="AuditEntry"/>), the <see cref="AuditWrite"/> input, and the kgsm event stream. No I/O —
/// all of it is unit-testable in isolation (the fidelity of the kgsm-event → action mapping + the
/// flat-actor-string round-trip is the M5 correctness risk the plan calls out).
/// </summary>
public static class AuditMapping
{
    /// <summary>
    /// Parse the kgsm event's flat <c>Actor</c> string into the structured <see cref="AuditActor"/>.
    /// The convention is <c>provider:name</c> (e.g. <c>discord:haru</c>, the command path stamps this);
    /// <see cref="AuditActor.Kind"/> is <em>derived</em> from the provider — Discord identities are
    /// humans (<c>user</c>), an <c>api</c> identity is a <c>token</c>, <c>system</c> is autonomous.
    /// A bare string with no prefix is kgsm's OS-user fallback (a human on the local host →
    /// <c>user</c>/<c>system</c>), and the literal <c>system</c> is an autonomous action. An
    /// unrecognized provider keeps the name but leaves <see cref="AuditActor.Provider"/> null rather
    /// than coerce it to an enum value (never fabricate).
    /// </summary>
    public static AuditActor ParseActor(string? flat)
    {
        flat = flat?.Trim();
        if (string.IsNullOrEmpty(flat))
            // No actor at all — kgsm always falls back to an OS user or "system", so this is defensive.
            return new AuditActor(ActorKind.System, "system", ActorProvider.System);

        int colon = flat.IndexOf(':');
        if (colon > 0 && colon < flat.Length - 1)
        {
            string provider = flat[..colon].ToLowerInvariant();
            string name = flat[(colon + 1)..];
            return provider switch
            {
                ActorProvider.Discord => new AuditActor(ActorKind.User, name, ActorProvider.Discord),
                ActorProvider.Api => new AuditActor(ActorKind.Token, name, ActorProvider.Api),
                ActorProvider.System => new AuditActor(ActorKind.System, name, ActorProvider.System),
                // A named provider we don't recognize: keep the name, but don't invent a provider.
                _ => new AuditActor(ActorKind.User, name, null),
            };
        }

        // No provider prefix: the literal "system" is an autonomous action; anything else is the
        // engine's OS-user fallback — a human on the local host (identity source = the system).
        return string.Equals(flat, "system", StringComparison.OrdinalIgnoreCase)
            ? new AuditActor(ActorKind.System, "system", ActorProvider.System)
            : new AuditActor(ActorKind.User, flat, ActorProvider.System);
    }

    /// <summary>An event/declared origin, normalized to the closed set or <see langword="null"/> (a
    /// surface we don't recognize, or none declared, is honest-unknown — never fabricated).</summary>
    public static string? NormalizeOrigin(string? origin)
    {
        origin = origin?.Trim().ToLowerInvariant();
        return AuditOrigin.IsKnown(origin) ? origin : null;
    }

    /// <summary>Build the <see cref="AuditWrite"/> for a kgsm server-lifecycle event — provenance off
    /// the envelope (<c>Actor</c>/<c>Origin</c>/<c>Timestamp</c>), target/scope off the instance name.</summary>
    public static AuditWrite FromServerEvent(
        EventDataBase data,
        string action,
        string severity,
        string summaryVerb,
        string hostId,
        IReadOnlyDictionary<string, string>? meta = null)
    {
        string instance = string.IsNullOrEmpty(data.InstanceName) ? "" : data.InstanceName;
        return new AuditWrite(
            // ts from the event when present; else when we recorded it (pre-enrichment kgsm only).
            Ts: data.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(data.Origin),
            Actor: ParseActor(data.Actor),
            Action: action,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: $"{summaryVerb} {instance}",
            Meta: meta);
    }

    /// <summary>
    /// Map a watchdog <c>instance_crashed</c> event (a desired-running process died and is being
    /// auto-restarted) to a <c>server.crash</c> row at <see cref="AuditSeverity.Warn"/>. Provenance
    /// is <c>system</c>/<c>system</c> off the envelope (an autonomous engine action — no human
    /// surface), which <see cref="ParseActor"/>/<see cref="NormalizeOrigin"/> handle unchanged.
    /// </summary>
    public static AuditWrite FromCrashEvent(InstanceCrashedData d, string hostId)
    {
        string instance = Instance(d);
        return CrashWrite(d, hostId, instance, AuditSeverity.Warn,
            $"{Display(instance)} crashed — auto-restarting");
    }

    /// <summary>
    /// Map a watchdog <c>instance_failed</c> event (the supervisor exhausted its restart retries and
    /// gave up — the escalation signal, staying down) to a <c>server.crash</c> row at
    /// <see cref="AuditSeverity.Danger"/>. Same single doc-given action as <see cref="FromCrashEvent"/>;
    /// the give-up is carried by the severity, the summary, and the exhausted restart count.
    /// </summary>
    public static AuditWrite FromFailedEvent(InstanceFailedData d, string hostId)
    {
        string instance = Instance(d);
        string tail = string.IsNullOrEmpty(d.Restarts) ? "" : $" after {d.Restarts} restart(s)";
        return CrashWrite(d, hostId, instance, AuditSeverity.Danger,
            $"{Display(instance)} crashed — supervisor gave up{tail}");
    }

    /// <summary>
    /// Map a kgsm <c>instance_ports_opened</c> event (the firewall echo — the engine opened
    /// the host-firewall ports on a confirmed success) to a <c>network.ports.open</c> row, recording
    /// the opened ports in <c>meta</c> in the canonical range-preserving form. Engine-sourced only:
    /// an instance's ports are opened by the supervisor when it starts and released when it stops,
    /// so the api never opens one itself and has nothing to direct-write.
    /// </summary>
    public static AuditWrite FromPortsOpenedEvent(InstancePortsOpenedData d, string hostId) =>
        PortsWrite(d, hostId, AuditAction.NetworkPortsOpen, "opened", d.Ports);

    /// <summary>
    /// Map a kgsm <c>instance_ports_closed</c> event (the firewall echo — the engine removed the
    /// host-firewall ports on a confirmed success, on a stop, an uninstall, or a standalone
    /// firewall-disable) to a <c>network.ports.close</c> row. Recording closes keeps the trail
    /// symmetric — a disable that isn't part of an uninstall would otherwise leave an
    /// opened-never-closed gap.
    /// </summary>
    public static AuditWrite FromPortsClosedEvent(InstancePortsClosedData d, string hostId) =>
        PortsWrite(d, hostId, AuditAction.NetworkPortsClose, "closed", d.Ports);

    /// <summary>
    /// Map a kgsm <c>instance_upnp_opened</c> event (the watchdog forwarded the instance's ports on
    /// the router via upnpc, on a confirmed exit-0) to a <c>network.upnp.open</c> row. DISTINCT from
    /// <see cref="FromPortsOpenedEvent"/>: a router NAT forward is a different fact from a host ufw
    /// rule, so it gets its own action — a reader can tell "the router forwards it" from "the firewall
    /// allows it". Always <c>system</c>/<c>system</c> (the autonomous daemon), handled unchanged by
    /// <see cref="ParseActor"/>/<see cref="NormalizeOrigin"/>. There is no api-issued UPnP command, so
    /// this action is cleanly watchdog-echo-only.
    /// </summary>
    public static AuditWrite FromUpnpOpenedEvent(InstanceUpnpOpenedData d, string hostId) =>
        UpnpWrite(d, hostId, AuditAction.NetworkUpnpOpen, "forwarded", d.Ports);

    /// <summary>
    /// Map a kgsm <c>instance_upnp_closed</c> event (the watchdog removed the router forward on a
    /// deliberate stop, confirmed exit-0) to a <c>network.upnp.close</c> row — the close counterpart of
    /// <see cref="FromUpnpOpenedEvent"/>, keeping the UPnP trail symmetric. A "nothing to delete" close
    /// emits no event upstream, so this never records a removal that didn't happen.
    /// </summary>
    public static AuditWrite FromUpnpClosedEvent(InstanceUpnpClosedData d, string hostId) =>
        UpnpWrite(d, hostId, AuditAction.NetworkUpnpClose, "removed", d.Ports);

    /// <summary>
    /// Map a kgsm <c>instance_upnp_reasserted</c> event (the watchdog's sweep found the router had
    /// dropped a running instance's forwards and put them back, confirmed exit-0) to a
    /// <c>network.upnp.reassert</c> row. Its own action rather than a second
    /// <see cref="FromUpnpOpenedEvent"/>: an open sits next to a start, whereas this records that the
    /// mapping disappeared with nothing on this host asking for it — a fact about the ROUTER, and the
    /// only one a reader can count to learn how unreliable theirs is. <c>meta.ports</c> carries the
    /// subset that was actually missing, so a partial loss never reads as the whole set having gone.
    /// Warn, not Info: unlike the open/close pair this is an unhealthy condition being papered over,
    /// and a run of them is worth noticing.
    /// </summary>
    public static AuditWrite FromUpnpReassertedEvent(InstanceUpnpReassertedData d, string hostId) =>
        UpnpWrite(d, hostId, AuditAction.NetworkUpnpReassert, "restored dropped", d.Ports,
            AuditSeverity.Warn);

    /// <summary>
    /// Map a kgsm <c>instance_update_available</c> event to a <c>server.update_available</c> row at
    /// <see cref="AuditSeverity.Info"/> — an engine echo like every other <c>server.*</c> action, with no
    /// second writer. kgsm records what each check found beside the instance and emits only for a version
    /// it has not announced before, so this is one row per new build, not one per check.
    /// <para>
    /// Provenance comes off the envelope: a scheduled sweep carries the leaf's own actor/origin, a check
    /// run by hand carries the person's — the same two independent axes as everywhere else, never derived
    /// from one another. <c>meta</c> keeps the installed and upstream versions so a reader learns the exact
    /// "from → to" pair rather than just that something was newer.
    /// </para>
    /// </summary>
    public static AuditWrite FromUpdateAvailableEvent(InstanceUpdateAvailableData d, string hostId)
    {
        string instance = string.IsNullOrEmpty(d.InstanceName) ? "" : d.InstanceName;
        IReadOnlyDictionary<string, string>? meta = null;
        if (!string.IsNullOrEmpty(d.CurrentVersion) || !string.IsNullOrEmpty(d.LatestVersion))
        {
            var m = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(d.CurrentVersion)) m["currentVersion"] = d.CurrentVersion;
            if (!string.IsNullOrEmpty(d.LatestVersion)) m["latestVersion"] = d.LatestVersion;
            meta = m;
        }

        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: AuditAction.ServerUpdateAvailable,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: $"update available for {Display(instance)}",
            Meta: meta);
    }

    /// <summary>
    /// Map a kgsm <c>instance_player_joined</c> event to a <c>player.join</c> row at
    /// <see cref="AuditSeverity.Info"/>. For our container images this is forwarded by the watchdog from
    /// the in-image detection shim; native detection (log-scraping) is the same shape (provenance
    /// <c>system</c>/<c>system</c> off the envelope, handled unchanged by <see cref="ParseActor"/>/
    /// <see cref="NormalizeOrigin"/>). The player identity (<c>playerId</c>/<c>playerName</c>/
    /// <c>playerAddr</c>, all nullable) and the <c>sessionKey</c> ride in <c>meta</c>; the row is scoped to
    /// the server (no player target kind), mirroring the crash mapper. Never fabricates a missing field.
    /// </summary>
    public static AuditWrite FromPlayerJoinedEvent(InstancePlayerJoinedData d, string hostId) =>
        PlayerWrite(d, hostId, AuditAction.PlayerJoin, "joined",
            d.PlayerId, d.PlayerName, d.PlayerAddr, d.SessionKey, reason: null);

    /// <summary>
    /// Map a kgsm <c>instance_player_left</c> event to a <c>player.leave</c> row — the leave counterpart of
    /// <see cref="FromPlayerJoinedEvent"/>, identical provenance/identity rules, plus the disconnect
    /// <c>reason</c> when the game's log carried one (never fabricated; kick/ban classification of this
    /// vocabulary is deferred to a future version).
    /// </summary>
    public static AuditWrite FromPlayerLeftEvent(InstancePlayerLeftData d, string hostId) =>
        PlayerWrite(d, hostId, AuditAction.PlayerLeave, "left",
            d.PlayerId, d.PlayerName, d.PlayerAddr, d.SessionKey, d.Reason);

    /// <summary>
    /// Map a kgsm moderation event (<c>instance_player_kicked</c>/<c>_banned</c>/<c>_unbanned</c>,
    /// kgsm-lib 2.1.0) to a <c>player.kick</c>/<c>player.ban</c>/<c>player.unban</c> row at
    /// <see cref="AuditSeverity.Warning"/> — an operator removing someone is a notable act, unlike
    /// the informational presence pair.
    /// </summary>
    /// <remarks>
    /// The subject is the <see cref="InstanceModerationDataBase.Target"/> the operator's request
    /// resolved to, and it rides in <c>meta</c> alongside the resolved <c>command</c>. It is
    /// deliberately <em>not</em> classified into a playerId/playerName/playerAddr slot: the blueprint
    /// declares what kind of identity it is, and guessing here would put an address in a name field
    /// on some game. Provenance comes off the envelope (the moderation endpoints stamp actor+origin
    /// onto the kgsm call), so unlike the presence echoes this is attributable to a person.
    /// </remarks>
    public static AuditWrite FromPlayerModerationEvent(
        InstanceModerationDataBase d, string hostId, string action, string verb)
    {
        string instance = Instance(d);

        Dictionary<string, string>? meta = null;
        if (!string.IsNullOrWhiteSpace(d.Target)) (meta ??= [])["target"] = d.Target;
        if (!string.IsNullOrWhiteSpace(d.Command)) (meta ??= [])["command"] = d.Command;

        // Removing someone's access is the notable act; restoring it is ordinary news.
        string severity = action == AuditAction.PlayerUnban ? AuditSeverity.Info : AuditSeverity.Warn;

        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: ModerationSummary(verb, d.Target, instance),
            Meta: meta);
    }

    /// <summary>
    /// A moderation row's sentence, with or without the person it named.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="AuditRedaction"/>, which builds the same row for a reader not permitted
    /// the target: one function means the two cannot word the same event differently, where two would
    /// drift the first time either sentence was reworded.
    /// </remarks>
    public static string ModerationSummary(string verb, string? target, string instance) =>
        $"{verb} {(string.IsNullOrWhiteSpace(target) ? "a player" : target)} on {Display(instance)}";

    /// <summary>
    /// Map a kgsm <c>instance_config_changed</c> event (kgsm-lib 1.22.0) to a <c>config.set</c> row at
    /// <see cref="AuditSeverity.Info"/>. The PATCH <c>/servers/{id}/config</c> path stamps actor+origin
    /// onto <c>SetInstanceConfigValue</c>, so this echo carries provenance off the envelope (handled
    /// unchanged by <see cref="ParseActor"/>/<see cref="NormalizeOrigin"/>) — engine-owned, no double-write.
    /// Only the changed <see cref="InstanceConfigChangedData.Key"/> rides in <c>meta</c>; the new
    /// <em>value</em> is intentionally never carried by the event (secret hygiene — instance config can hold
    /// passwords/tokens), so this row can never leak one. A blank key degrades to a key-less summary + null
    /// meta (defensive — the event guarantees a non-null key), never a fabricated placeholder.
    /// </summary>
    /// <summary>
    /// Whether a changed config key is one of the server note's two <em>attribution</em> keys
    /// (<c>note_updated_by</c> / <c>note_updated_at</c>). A note write touches three keys, so the
    /// engine emits three <c>instance_config_changed</c> events for one operator action; both audit
    /// paths (the live consumer and the monitor-history shaping) drop these two so the feed shows the
    /// single "set config 'note'" row a reader expects. The body's own event is never suppressed —
    /// changing a player-facing note stays fully audited.
    /// </summary>
    public static bool IsNoteAttributionKey(string? key) =>
        string.Equals(key, InstanceNote.UpdatedByKey, StringComparison.Ordinal)
        || string.Equals(key, InstanceNote.UpdatedAtKey, StringComparison.Ordinal);

    /// <summary>
    /// Whether a changed config key is the instance's display label. A rename writes that one key, so the
    /// engine emits an <c>instance_config_changed</c> for it beside the richer
    /// <c>instance_display_name_changed</c>; both audit paths drop this one so a rename reads as the
    /// single <c>server.rename</c> row that actually says what the label went from and to.
    /// </summary>
    public static bool IsDisplayNameKey(string? key) =>
        string.Equals(key, DisplayNameConfigKey, StringComparison.Ordinal);

    /// <summary>The instance-config key the display label is stored under.</summary>
    public const string DisplayNameConfigKey = "display_name";

    /// <summary>
    /// Map a kgsm <c>instance_display_name_changed</c> event to a <c>server.rename</c> row at
    /// <see cref="AuditSeverity.Info"/>. The label is decoration, so this is not a warning — but it is
    /// recorded, because a feed that only ever showed the current label would make its own older rows
    /// unreadable to whoever remembers the previous one.
    /// <para>Target and <c>serverId</c> are the instance <b>id</b>, which the rename did not touch: the
    /// audit trail keys on immutable identity, so every row about this server — before and after —
    /// still joins to it. Both labels ride <c>meta</c> under the engine's own field names, so the
    /// redactor's per-field lookup keeps working if either is ever reclassified upstream; the engine
    /// classifies both Public today, on the grounds that a label is text somebody chose to be read.
    /// A cleared label is reported as the id the instance now reads as rather than as an empty
    /// string.</para>
    /// </summary>
    public static AuditWrite FromDisplayNameChangedEvent(InstanceDisplayNameChangedData d, string hostId)
    {
        string instance = Instance(d);
        string from = Label(d.OldDisplayName, instance);
        string to = Label(d.NewDisplayName, instance);
        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: AuditAction.ServerRename,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: $"renamed {Display(instance)} from '{from}' to '{to}'",
            Meta: new Dictionary<string, string>
            {
                ["oldDisplayName"] = from,
                ["newDisplayName"] = to,
            });
    }

    // An instance with no label of its own reads as its id — the same substitution kgsm-lib's
    // Instance.DisplayName makes — so a cleared label is reported as what it now shows, never as blank.
    private static string Label(string? label, string instance) =>
        string.IsNullOrEmpty(label) ? Display(instance) : label;

    public static AuditWrite FromConfigChangedEvent(InstanceConfigChangedData d, string hostId)
    {
        string instance = Instance(d);
        string key = string.IsNullOrEmpty(d.Key) ? "" : d.Key;
        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: AuditAction.ConfigSet,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: string.IsNullOrEmpty(key)
                ? $"config changed for {Display(instance)}"
                : $"set config '{key}' for {Display(instance)}",
            // KEY ONLY — never the value (the event doesn't carry it; this is the secret-hygiene guard).
            Meta: string.IsNullOrEmpty(key) ? null : new Dictionary<string, string> { ["key"] = key });
    }

    /// <summary>
    /// Map a kgsm <c>instance_input_sent</c> event (kgsm-lib 1.24.0) to a <c>console.input</c> row at
    /// <see cref="AuditSeverity.Info"/>. The POST <c>/servers/{id}/console</c> path stamps actor+origin
    /// onto <c>SendInput</c>, so this echo carries provenance off the envelope (handled unchanged by
    /// <see cref="ParseActor"/>/<see cref="NormalizeOrigin"/>) — engine-owned, no double-write. UNLIKE
    /// <see cref="FromConfigChangedEvent"/> (key only), the FULL command text rides in <c>meta.command</c>
    /// and a truncated form in the summary, on purpose: the trail records exactly what an operator ran
    /// (console commands are admin-level). A blank command degrades to a command-less summary + null meta
    /// (defensive — the event guarantees a non-empty command), never a fabricated placeholder.
    /// </summary>
    public static AuditWrite FromInputSentEvent(InstanceInputSentData d, string hostId)
    {
        string instance = Instance(d);
        string command = string.IsNullOrEmpty(d.Command) ? "" : d.Command;
        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: AuditAction.ConsoleInput,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: ConsoleInputSummary(command, instance),
            // FULL command (untruncated) — the deliberate divergence from config.set's key-only rule.
            Meta: string.IsNullOrEmpty(command) ? null : new Dictionary<string, string> { ["command"] = command });
    }

    /// <summary>
    /// A console row's sentence, with or without what was typed. The command is truncated for the
    /// sentence and carried whole in <c>meta</c>.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="AuditRedaction"/> for the same reason as
    /// <see cref="ModerationSummary"/>: a reader not permitted the command reads the sentence this
    /// produces for an event that carried none, rather than a second wording of it.
    /// </remarks>
    public static string ConsoleInputSummary(string? command, string instance)
    {
        if (string.IsNullOrEmpty(command))
            return $"sent a console command to {Display(instance)}";

        string shown = command.Length > 80 ? command[..79] + "…" : command;
        return $"ran '{shown}' on {Display(instance)}";
    }

    /// <summary>
    /// Map a kgsm <c>blueprint_created</c> event to a <c>blueprint.write</c> row — a blueprint file that
    /// did not exist in the user directory before. Provenance rides off the envelope (the PUT
    /// <c>/library/{id}/file</c> path threads actor+origin into the emit, so the echo carries the real
    /// admin rather than the service account) — engine-owned, no double-write.
    /// </summary>
    public static AuditWrite FromBlueprintCreatedEvent(BlueprintCreatedData d, string hostId) =>
        BlueprintWrite(d, hostId, d.Tier, AuditAction.BlueprintWrite, AuditSeverity.Success,
            // "overrode X" vs "created X" are materially different facts to a reader: the first means this
            // host has stopped tracking the shipped definition of a game it already had, the second means a
            // game was added. An unknown override state claims neither.
            d.OverridesSystem switch
            {
                true => $"overrode blueprint {DisplayBlueprint(d)}",
                _ => $"created blueprint {DisplayBlueprint(d)}",
            },
            ("overridesSystem", Tri(d.OverridesSystem)), ("runtime", d.Runtime));

    /// <summary>
    /// Map a kgsm <c>blueprint_updated</c> event to a <c>blueprint.write</c> row — the same action as
    /// <see cref="FromBlueprintCreatedEvent"/> at <see cref="AuditSeverity.Info"/>, since "the blueprint
    /// file changed" is one fact; whether the file already existed is carried by the summary, not by a
    /// second action nobody would filter on separately.
    /// </summary>
    public static AuditWrite FromBlueprintUpdatedEvent(BlueprintUpdatedData d, string hostId) =>
        BlueprintWrite(d, hostId, d.Tier, AuditAction.BlueprintWrite, AuditSeverity.Info,
            $"edited blueprint {DisplayBlueprint(d)}",
            ("overridesSystem", Tri(d.OverridesSystem)), ("runtime", d.Runtime));

    /// <summary>
    /// Map a kgsm <c>blueprint_removed</c> event to a <c>blueprint.revert</c> row. Warn, not info: the
    /// user-directory file is gone. <c>revertedToSystem</c> says whether a shipped blueprint took over
    /// (the library editor's revert, which only ever runs when one exists) or the blueprint left this host
    /// entirely — a distinction the summary makes rather than leaving a reader to assume the safer one.
    /// </summary>
    public static AuditWrite FromBlueprintRemovedEvent(BlueprintRemovedData d, string hostId) =>
        BlueprintWrite(d, hostId, d.Tier, AuditAction.BlueprintRevert, AuditSeverity.Warn,
            d.RevertedToSystem switch
            {
                true => $"reverted blueprint {DisplayBlueprint(d)} to the shipped version",
                false => $"removed blueprint {DisplayBlueprint(d)}",
                null => $"removed the local copy of blueprint {DisplayBlueprint(d)}",
            },
            ("revertedToSystem", Tri(d.RevertedToSystem)));

    /// <summary>
    /// Map a kgsm <c>library_added</c> event to a <c>library.add</c> row — the host gained somewhere to put
    /// servers.
    /// </summary>
    /// <remarks>
    /// Engine-owned echo: the CRUD path stamps actor+origin onto the kgsm call, so this carries the real
    /// admin rather than the service account, and nothing is direct-written for it.
    /// </remarks>
    public static AuditWrite FromLibraryAddedEvent(LibraryAddedData d, string hostId) =>
        LibraryWrite(d, hostId, AuditAction.LibraryAdd, AuditSeverity.Success,
            $"registered library {DisplayLibrary(d)}", ("path", d.Path));

    /// <summary>
    /// Map a kgsm <c>library_removed</c> event to a <c>library.remove</c> row.
    /// </summary>
    /// <remarks>
    /// Warn, not info, and the summary says so: deregistering touches no file, so the instances that
    /// lived there are still on the disk and now resolve to no library. That is a recoverable state and a
    /// confusing one, and the trail is where somebody works out when it started.
    /// </remarks>
    public static AuditWrite FromLibraryRemovedEvent(LibraryRemovedData d, string hostId) =>
        LibraryWrite(d, hostId, AuditAction.LibraryRemove, AuditSeverity.Warn,
            $"deregistered library {DisplayLibrary(d)} — its files are untouched", ("path", d.Path));

    /// <summary>
    /// Map a <c>library_renamed</c> event to a <c>library.rename</c> row.
    /// </summary>
    /// <remarks>
    /// The one library action this API writes rather than echoes: a rename touches the registry and the
    /// marker, the engine emits nothing, and without this row the name in every earlier row would refer
    /// to something a reader could no longer find. Instances are unaffected — each records its library's
    /// path, not its name.
    /// </remarks>
    public static AuditWrite FromLibraryRenamedEvent(LibraryOutcomeEventData d, string hostId) =>
        LibraryWrite(d, hostId, AuditAction.LibraryRename, AuditSeverity.Info,
            $"renamed library {DisplayLibrary(d)} to {(string.IsNullOrEmpty(d.NewName) ? "a new name" : d.NewName)}",
            ("newName", d.NewName));

    /// <summary>
    /// Map a <c>library_failed</c> event to a <c>library.failed</c> row — a mutation that did not happen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine emits an event when a library verb <em>works</em>; one that fails exits non-zero and
    /// emits nothing, so there is no echo to ride and no double-write risk. Warn rather than danger:
    /// the ordinary case is the engine refusing to deregister a library instances still live in, which
    /// is the protection working, not a fault.
    /// </para>
    /// <para>
    /// The engine's own sentence rides in <c>meta</c> and never in the summary — it names the instances
    /// that blocked a removal, and a reworded kgsm message must change what a reader can dig into, not
    /// how the feed reads.
    /// </para>
    /// </remarks>
    public static AuditWrite FromLibraryFailedEvent(LibraryOutcomeEventData d, string hostId) =>
        LibraryWrite(d, hostId, AuditAction.LibraryFailed, AuditSeverity.Warn,
            $"could not {LibraryWord(d.Verb)} library {DisplayLibrary(d)}",
            ("verb", d.Verb), ("path", d.Path), ("newName", d.NewName), ("error", d.Error),
            ("exitCode", d.ExitCode?.ToString(CultureInfo.InvariantCulture)));

    // The four library rows differ only in action/severity/summary/meta. TARGET is the library, and
    // ServerId is deliberately NULL: a root holds servers without being one, and filling in a serverId
    // would make `GET /audit?serverId=` return a disk registration that never touched that instance.
    private static AuditWrite LibraryWrite(
        LibraryEventDataBase d, string hostId, string action, string severity, string summary,
        params (string Key, string? Value)[] extra)
    {
        string name = string.IsNullOrEmpty(d.LibraryName) ? "" : d.LibraryName;
        Dictionary<string, string>? meta = null;
        if (!string.IsNullOrEmpty(name)) (meta ??= [])["name"] = name;
        // A blank value is OMITTED, never stored as "" — an absent key reads as unknown rather than as an
        // empty answer.
        foreach ((string key, string? value) in extra)
            if (!string.IsNullOrEmpty(value)) (meta ??= [])[key] = value;

        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Library, name, name),
            ServerId: null,
            HostId: hostId,
            Summary: summary,
            Meta: meta);
    }

    // The library verb as a sentence uses it. A verb the set does not name is printed as it was recorded
    // rather than replaced — a row about a failure must be able to say what failed.
    private static string LibraryWord(string? verb) => verb switch
    {
        "add" => "register",
        "remove" => "deregister",
        "rename" => "rename",
        { Length: > 0 } named => named,
        _ => "change",
    };

    // A human-facing fallback for the summary line only, mirroring Display(instance) — the target/meta
    // keep the raw, possibly-empty value.
    private static string DisplayLibrary(LibraryEventDataBase d) =>
        string.IsNullOrEmpty(d.LibraryName) ? "(unnamed)" : d.LibraryName;

    // The three blueprint rows differ only in action/severity/summary/meta. TARGET is the blueprint, and
    // ServerId is deliberately NULL: a blueprint is the template servers are installed from, not a server,
    // and filling in serverId would make `GET /audit?serverId=` return an edit that never touched that
    // instance. Content is never recorded — name/tier/runtime/override state only, the file.write rule.
    private static AuditWrite BlueprintWrite(
        BlueprintEventDataBase d, string hostId, BlueprintTier tier, string action, string severity,
        string summary, params (string Key, string? Value)[] extra)
    {
        string name = string.IsNullOrEmpty(d.BlueprintName) ? "" : d.BlueprintName;
        var meta = new Dictionary<string, string>
        {
            ["tier"] = tier == BlueprintTier.User ? "user" : "system",
        };
        if (!string.IsNullOrEmpty(name)) meta["name"] = name;
        // A blank/absent value is OMITTED, never stored as "" — an unknown runtime or override state is
        // an absent key, which reads as unknown rather than as an empty answer.
        foreach ((string key, string? value) in extra)
            if (!string.IsNullOrEmpty(value)) meta[key] = value;

        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Blueprint, name, name),
            ServerId: null,
            HostId: hostId,
            Summary: summary,
            Meta: meta);
    }

    // A tri-state flag as meta: "true"/"false", or OMITTED when the emitter could not determine it. Never
    // collapsed to "false" — "we don't know whether this shadows a shipped blueprint" is not "it doesn't".
    private static string? Tri(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => null,
    };

    // Summary-line fallback for a blueprint, mirroring Display(instance) — ids/targets keep the raw,
    // possibly-empty value; only the human-facing sentence gets a placeholder.
    private static string DisplayBlueprint(BlueprintEventDataBase d) =>
        string.IsNullOrEmpty(d.BlueprintName) ? "blueprint" : d.BlueprintName;

    // Join/left differ only in action + summary verb — build the row once. The summary names the player
    // by display name, falling back to the stable id, then a generic label (never fabricates an identity;
    // at-least-one-non-null is the emitting side's guarantee, this is defensive).
    private static AuditWrite PlayerWrite(
        EventDataBase d, string hostId, string action, string verb,
        string? playerId, string? playerName, string? playerAddr, string? sessionKey, string? reason)
    {
        string instance = Instance(d);
        string who = !string.IsNullOrEmpty(playerName) ? playerName!
            : !string.IsNullOrEmpty(playerId) ? playerId!
            : "a player";
        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: $"{who} {verb} {Display(instance)}",
            Meta: PlayerMeta(playerId, playerName, playerAddr, sessionKey, reason));
    }

    // Meta off a player event (id/name/addr/reason, all nullable, plus sessionKey); empties dropped, null
    // when nothing is present — never store "". The honest identity, never fabricated. Rides the existing
    // Meta JSON column — no schema change.
    private static IReadOnlyDictionary<string, string>? PlayerMeta(
        string? id, string? name, string? addr, string? sessionKey, string? reason)
    {
        Dictionary<string, string>? meta = null;
        if (!string.IsNullOrEmpty(id)) (meta ??= [])["playerId"] = id!;
        if (!string.IsNullOrEmpty(name)) (meta ??= [])["playerName"] = name!;
        if (!string.IsNullOrEmpty(addr)) (meta ??= [])["playerAddr"] = addr!;
        if (!string.IsNullOrEmpty(sessionKey)) (meta ??= [])["sessionKey"] = sessionKey!;
        if (!string.IsNullOrEmpty(reason)) (meta ??= [])["reason"] = reason!;
        return meta;
    }

    // Open/close differ only in action + summary verb — build the row once.
    private static AuditWrite PortsWrite(
        EventDataBase d, string hostId, string action, string verb, IReadOnlyList<PortMapping> ports)
    {
        string instance = Instance(d);
        string formatted = FormatPorts(ports);
        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: $"{verb} firewall ports for {Display(instance)}",
            Meta: string.IsNullOrEmpty(formatted)
                ? null
                : new Dictionary<string, string> { ["ports"] = formatted });
    }

    // UPnP open/close differ only in action + summary verb — build the row once. Same structured
    // ports meta as PortsWrite, but a router-forward summary ("forwarded/removed UPnP ports") so the
    // audit feed reads distinctly from the host-firewall rows.
    private static AuditWrite UpnpWrite(
        EventDataBase d, string hostId, string action, string verb, IReadOnlyList<PortMapping> ports,
        string severity = AuditSeverity.Info)
    {
        string instance = Instance(d);
        string formatted = FormatPorts(ports);
        return new AuditWrite(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: $"{verb} UPnP ports for {Display(instance)}",
            Meta: string.IsNullOrEmpty(formatted)
                ? null
                : new Dictionary<string, string> { ["ports"] = formatted });
    }

    /// <summary>Render a set of <see cref="PortMapping"/>s to a compact human string
    /// (<c>"2456-2458/udp, 27015/tcp"</c>) for an audit <c>meta</c> entry; empty for an empty set.</summary>
    public static string FormatPorts(IReadOnlyList<PortMapping>? ports) =>
        ports is null || ports.Count == 0
            ? ""
            : string.Join(", ", ports
                .Where(p => p is not null)
                .Select(p => p.Start == p.End
                    ? $"{p.Start}/{p.Protocol}"
                    : $"{p.Start}-{p.End}/{p.Protocol}"));

    // The two crash events share everything but severity + summary — build the row once.
    private static AuditWrite CrashWrite(
        EventDataBase d, string hostId, string instance, string severity, string summary) =>
        new(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: AuditAction.ServerCrash,
            Severity: severity,
            Target: new AuditTarget(AuditTargetKind.Server, instance, instance),
            ServerId: instance,
            HostId: hostId,
            Summary: summary,
            Meta: CrashMeta(d));

    // Meta off the two crash event types (both expose ExitCode + Restarts strings); empties dropped,
    // null when nothing material — never store "".
    private static IReadOnlyDictionary<string, string>? CrashMeta(EventDataBase d)
    {
        (string ExitCode, string Restarts) f = d switch
        {
            InstanceCrashedData c => (c.ExitCode, c.Restarts),
            InstanceFailedData c => (c.ExitCode, c.Restarts),
            _ => ("", ""),
        };
        Dictionary<string, string>? meta = null;
        if (!string.IsNullOrEmpty(f.ExitCode)) (meta ??= [])["exitCode"] = f.ExitCode;
        if (!string.IsNullOrEmpty(f.Restarts)) (meta ??= [])["restarts"] = f.Restarts;
        return meta;
    }

    private static string Instance(EventDataBase d) =>
        string.IsNullOrEmpty(d.InstanceName) ? "" : d.InstanceName;

    // A human-facing fallback for the summary line only (ids/scope keep the raw, possibly-empty value
    // to match FromServerEvent); a crash/ports event always carries an instance in practice.
    private static string Display(string instance) =>
        string.IsNullOrEmpty(instance) ? "instance" : instance;

    /// <summary>Map a persisted row to its wire record (deserializing the <c>meta</c> JSON blob).</summary>
    public static AuditRecord ToRecord(AuditEntry e)
    {
        IReadOnlyDictionary<string, string>? meta = null;
        if (!string.IsNullOrEmpty(e.Meta))
        {
            try { meta = JsonSerializer.Deserialize<Dictionary<string, string>>(e.Meta); }
            catch (JsonException) { meta = null; }
        }

        AuditTarget? target = e.TargetKind is null
            ? null
            : new AuditTarget(e.TargetKind, e.TargetId ?? "", e.TargetName);

        return new AuditRecord(
            e.Id, e.Ts, e.Origin,
            new AuditActor(e.ActorKind, e.ActorName, e.ActorProvider),
            e.Action, e.Severity, target, e.ServerId, e.HostId, e.Summary, meta);
    }

    /// <summary>
    /// Map an <see cref="AuditWrite"/> + an externally-supplied id directly to the wire record — no EF
    /// round-trip. Two callers need this: <see cref="AuditService.PublishLive"/> (a kgsm engine event,
    /// post Phase-C, is announced on the <c>audit</c> WS topic but never persisted locally — the id is
    /// the deterministic <c>AuditId.ForEvent</c> value via <see cref="EngineEventIdTracker"/>) and
    /// <see cref="EngineEventShaping"/> (shaping a monitor-persisted raw event at <c>GET /audit</c> read
    /// time — the id is the monitor's own stored id for that event). Both must reuse the SAME id the
    /// monitor computed/stored for the identical envelope, so a live push and a later paginated read of
    /// the same fact carry one identity.
    /// </summary>
    public static AuditRecord ToRecordDirect(AuditWrite w, string id) =>
        new(id, w.Ts, w.Origin, w.Actor, w.Action, w.Severity, w.Target, w.ServerId, w.HostId, w.Summary, w.Meta);

    /// <summary>Map an <see cref="AuditWrite"/> + its assigned public id to the EF row (serializing
    /// <c>meta</c> to a JSON blob).</summary>
    public static AuditEntry ToEntity(AuditWrite w, string id) => new()
    {
        Id = id,
        Ts = w.Ts,
        Origin = w.Origin,
        ActorKind = w.Actor.Kind,
        ActorName = w.Actor.Name,
        ActorProvider = w.Actor.Provider,
        Action = w.Action,
        Severity = w.Severity,
        TargetKind = w.Target?.Kind,
        TargetId = w.Target?.Id,
        TargetName = w.Target?.Name,
        ServerId = w.ServerId,
        HostId = w.HostId,
        Summary = w.Summary,
        Meta = w.Meta is null || w.Meta.Count == 0 ? null : JsonSerializer.Serialize(w.Meta),
    };

    /// <summary>
    /// Map a kgsm-monitor <c>host_threshold_breached</c> event to a <c>host.threshold.breach</c> row.
    /// </summary>
    /// <remarks>
    /// The monitor records the measurement; the wording, the severity and the target are applied here, at
    /// read time. That split is the point: the journal holds what was measured, so two surfaces can phrase
    /// the same episode differently without either being wrong, and neither can be made wrong by the
    /// other's vocabulary being frozen into the record.
    /// <para>
    /// The row's timestamp is <c>OpenedTs</c> — the moment the condition changed — not the moment the line
    /// was written. A reader scanning the trail has to see the breach where it happened.
    /// </para>
    /// </remarks>
    public static AuditWrite FromThresholdBreachedEvent(HostThresholdBreachedData d, string hostId)
    {
        bool hostScope = !string.Equals(d.Scope, "server", StringComparison.Ordinal);
        string noun = ConditionDisplay.Noun(d.Metric);
        string subject = hostScope
            ? (string.IsNullOrEmpty(d.Ref) ? hostId : d.Ref!)
            : (d.ServerId ?? "server");

        var meta = ThresholdMeta(d);
        meta["value"] = ConditionDisplay.Format(d.Metric, d.OpenValue);

        return new AuditWrite(
            Ts: DateTimeOffset.FromUnixTimeMilliseconds(d.OpenedTs),
            Origin: AuditOrigin.System,
            Actor: ParseActor(MonitorActor),
            Action: AuditAction.HostThresholdBreach,
            // As loud as the band it reached. A condition that touched danger and eased back to warn was
            // still a danger-band episode, which is why the peak band decides and not the current one.
            Severity: string.Equals(d.PeakBand, "danger", StringComparison.Ordinal)
                ? AuditSeverity.Danger
                : AuditSeverity.Warn,
            Target: hostScope
                ? new AuditTarget(AuditTargetKind.Host, hostId, hostId)
                : new AuditTarget(AuditTargetKind.Server, d.ServerId ?? "", d.ServerId),
            ServerId: hostScope ? null : d.ServerId,
            HostId: hostId,
            Summary: $"{subject} {noun} crossed {ConditionDisplay.Format(d.Metric, d.Threshold)}",
            Meta: meta);
    }

    /// <summary>
    /// Map a kgsm-monitor <c>host_threshold_cleared</c> event to a <c>host.threshold.clear</c> row.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>A clear is not always a recovery.</b> An episode that ended because its rule was retuned,
    /// disabled or removed did not come back under its line — the value was never observed to come down at
    /// all. Saying "back to normal" for those would report a measurement nobody took, so the close reason
    /// picks the sentence.
    /// </remarks>
    public static AuditWrite FromThresholdClearedEvent(HostThresholdClearedData d, string hostId)
    {
        bool hostScope = !string.Equals(d.Scope, "server", StringComparison.Ordinal);
        string noun = ConditionDisplay.Noun(d.Metric);
        string subject = hostScope
            ? (string.IsNullOrEmpty(d.Ref) ? hostId : d.Ref!)
            : (d.ServerId ?? "server");

        long heldSec = Math.Max(0, (d.ClosedTs - d.OpenedTs) / 1000);

        var meta = ThresholdMeta(d);
        meta["heldSec"] = heldSec.ToString(CultureInfo.InvariantCulture);
        meta["value"] = ConditionDisplay.Format(d.Metric, d.CloseValue ?? d.PeakValue);
        if (!string.IsNullOrEmpty(d.CloseReason)) meta["reason"] = d.CloseReason!;

        string summary = d.CloseReason switch
        {
            // The rule was retuned, disabled or removed while this was firing.
            "unwatched" => $"{subject} {noun} no longer watched after {ConditionDisplay.Duration(heldSec)} over its threshold",
            // The monitor restarted while this was firing. The condition may well have still been true.
            "interrupted" => $"{subject} {noun} still over its threshold when monitoring stopped, after {ConditionDisplay.Duration(heldSec)}",
            _ => $"{subject} {noun} back to normal after {ConditionDisplay.Duration(heldSec)}",
        };

        return new AuditWrite(
            Ts: DateTimeOffset.FromUnixTimeMilliseconds(d.ClosedTs),
            Origin: AuditOrigin.System,
            Actor: ParseActor(MonitorActor),
            Action: AuditAction.HostThresholdClear,
            // A recovery is information, never a warning.
            Severity: AuditSeverity.Info,
            Target: hostScope
                ? new AuditTarget(AuditTargetKind.Host, hostId, hostId)
                : new AuditTarget(AuditTargetKind.Server, d.ServerId ?? "", d.ServerId),
            ServerId: hostScope ? null : d.ServerId,
            HostId: hostId,
            Summary: summary,
            Meta: meta);
    }

    /// <summary>The meta both threshold rows share.</summary>
    private static Dictionary<string, string> ThresholdMeta(HostThresholdEventDataBase d)
    {
        var meta = new Dictionary<string, string>
        {
            ["episodeId"] = d.EpisodeId,
            ["ruleKey"] = d.RuleKey,
            ["metric"] = d.Metric,
            ["threshold"] = ConditionDisplay.Format(d.Metric, d.Threshold),
            ["peak"] = ConditionDisplay.Format(d.Metric, d.PeakValue),
        };

        if (!string.IsNullOrEmpty(d.Ref)) meta["ref"] = d.Ref!;
        return meta;
    }

    /// <summary>
    /// The identity a threshold row carries. The monitor established the fact; this API only relays it,
    /// and a bare <c>system</c> could not tell a measured breach from any other unattended action.
    /// </summary>
    private const string MonitorActor = "system:monitor";

    // ---- this API's own events -----------------------------------------------------------------
    //
    // The Control Panel performs these itself — signing somebody in, changing an account's authority,
    // reconfiguring a leaf — so it records the fact and shapes the sentence here, at read time, the
    // same as for any other producer's journal. Nothing about these mappers knows they are reading
    // this API's own writing.

    /// <summary>Map an <c>auth_login</c> / <c>auth_logout</c> / <c>auth_cluster_session</c> event.</summary>
    /// <remarks>
    /// What separates "logged in" from "signed in with a password" is the identity PROVIDER, which is
    /// on the record — not which code path ran. A wording change here therefore applies to every row
    /// ever written, rather than only to the ones written after it.
    /// </remarks>
    public static AuditWrite FromAuthSessionEvent(AuthSessionEventData d, string type, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        string who = string.IsNullOrEmpty(d.Username) ? d.Identity : d.Username;
        (string action, string summary) = type switch
        {
            ApiJournal.LogoutEvent => (AuditAction.AuthLogout, $"{who} logged out"),
            ApiJournal.ClusterSessionEvent => (AuditAction.AuthClusterSession,
                string.IsNullOrEmpty(d.PeerNode)
                    ? $"{who} joined via a cluster vouch"
                    : $"{who} joined via a cluster vouch from node '{d.PeerNode}'"),
            // A local credential and a bounce through an external provider are different enough that a
            // reader auditing access wants them apart, and the provider is what says which.
            _ when string.Equals(d.Provider, "local", StringComparison.OrdinalIgnoreCase) =>
                (AuditAction.AuthLogin, $"{who} signed in with a password"),
            _ => (AuditAction.AuthLogin, $"{who} logged in"),
        };

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(d.Tier)) meta["tier"] = d.Tier!;
        if (!string.IsNullOrEmpty(d.Sid)) meta["sid"] = d.Sid!;
        if (!string.IsNullOrEmpty(d.UserId)) meta["userId"] = d.UserId!;
        if (!string.IsNullOrEmpty(d.Identity)) meta["identity"] = d.Identity;
        if (!string.IsNullOrEmpty(d.UserAgent)) meta["userAgent"] = d.UserAgent!;
        if (!string.IsNullOrEmpty(d.PeerNode)) meta["peerNode"] = d.PeerNode!;

        return ApiWrite(d, action, AuditSeverity.Info, target: null, hostId, summary, meta);
    }

    /// <summary>Map an <c>auth_session_revoked</c> event to one of the three revocation actions.</summary>
    public static AuditWrite FromSessionRevokedEvent(AuthSessionRevokedData d, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        string who = ActorName(d.Actor) ?? d.Username;
        int count = d.Count ?? 1;
        string sessions = count == 1 ? "a session" : $"{count} sessions";

        // An admin revoking somebody else's session is the substantial-power case and reads louder than
        // a person managing their own, which is routine.
        (string action, string severity, string summary) = d.Scope switch
        {
            "all" => (AuditAction.AuthSessionRevokeAll, AuditSeverity.Info,
                $"{who} logged out everywhere ({count} session(s))"),
            "admin" => (AuditAction.AuthSessionRevokeAdmin, AuditSeverity.Warn,
                $"{who} revoked {sessions} belonging to {d.Username}"),
            _ => (AuditAction.AuthSessionRevoke, AuditSeverity.Info, $"{who} revoked {sessions}"),
        };

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(d.Sid)) meta["sid"] = d.Sid!;
        if (!string.IsNullOrEmpty(d.UserId)) meta["userId"] = d.UserId!;
        if (d.Count is { } n) meta["count"] = n.ToString(CultureInfo.InvariantCulture);

        return ApiWrite(d, action, severity, target: null, hostId, summary, meta);
    }

    /// <summary>Map one of the six <c>user_*</c> events to its account action.</summary>
    /// <remarks>
    /// ⚠ These are the trail's most sensitive rows: with the account store as this host's sole
    /// authority, a tier change here is the ONLY way anybody's permissions ever move.
    /// </remarks>
    public static AuditWrite FromUserAccountEvent(UserAccountEventData d, string type, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        string who = ActorName(d.Actor);
        string name = d.Username;
        (string action, string severity, string summary) = type switch
        {
            ApiJournal.UserProvisionedEvent => (AuditAction.UserProvision, AuditSeverity.Warn,
                string.IsNullOrEmpty(d.ToTier)
                    ? $"{who} created the account '{name}'"
                    : $"{who} created the account '{name}' with the {d.ToTier} tier"),

            ApiJournal.UserApprovedEvent => (AuditAction.UserApprove, AuditSeverity.Warn,
                $"{who} approved the account '{name}'"),

            // Two different endings share this event, told apart by where the account landed rather
            // than by a verb chosen at write time: switched off, or put back to awaiting approval.
            ApiJournal.UserDisabledEvent when
                !string.Equals(d.ToStatus, "disabled", StringComparison.OrdinalIgnoreCase) =>
                (AuditAction.UserDisable, AuditSeverity.Warn,
                    $"{who} returned the account '{name}' to awaiting approval"),
            ApiJournal.UserDisabledEvent => (AuditAction.UserDisable, AuditSeverity.Danger,
                $"{who} disabled the account '{name}'"),

            ApiJournal.UserTierChangedEvent => (AuditAction.UserTierChange, AuditSeverity.Warn,
                $"{who} changed '{name}' from {d.FromTier ?? "none"} to {d.ToTier ?? "none"}"),

            ApiJournal.UserDeletedEvent => (AuditAction.UserDelete, AuditSeverity.Danger,
                $"{who} deleted the account '{name}'"),

            // Somebody else setting your password reads completely differently from you setting it —
            // it is the only signal an account takeover leaves — so the two get different sentences
            // and different weights.
            _ when d.ByHolder == true => (AuditAction.UserPassword, AuditSeverity.Info,
                $"{who} changed their own password"),
            _ => (AuditAction.UserPassword, AuditSeverity.Warn, $"{who} set the password on '{name}'"),
        };

        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = name };
        if (!string.IsNullOrEmpty(d.UserId)) meta["userId"] = d.UserId!;
        if (!string.IsNullOrEmpty(d.FromTier)) meta["fromTier"] = d.FromTier!;
        if (!string.IsNullOrEmpty(d.ToTier)) meta["toTier"] = d.ToTier!;
        if (!string.IsNullOrEmpty(d.FromStatus)) meta["from"] = d.FromStatus!;
        if (!string.IsNullOrEmpty(d.ToStatus)) meta["to"] = d.ToStatus!;
        if (d.ByHolder is { } held) meta["by"] = held ? "self" : "admin";

        AuditTarget? target = string.IsNullOrEmpty(d.UserId)
            ? null
            : new AuditTarget("user", d.UserId!, name);

        return ApiWrite(d, action, severity, target, hostId, summary, meta);
    }

    /// <summary>Map an <c>identity_linked</c> / <c>identity_unlinked</c> event.</summary>
    /// <remarks>
    /// A link is a privilege event: afterwards, whoever controls that provider account can sign in as
    /// this one.
    /// </remarks>
    public static AuditWrite FromIdentityEvent(IdentityLinkEventData d, string type, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        bool linked = string.Equals(type, ApiJournal.IdentityLinkedEvent, StringComparison.Ordinal);
        string who = ActorName(d.Actor);
        string summary = linked
            ? $"{who} connected a {d.Provider} account to '{d.Username}'"
            : $"{who} disconnected a {d.Provider} account from '{d.Username}'";

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = d.Username,
            ["provider"] = d.Provider,
        };
        if (!string.IsNullOrEmpty(d.UserId)) meta["userId"] = d.UserId!;
        if (!string.IsNullOrEmpty(d.Handle)) meta["identity"] = d.Handle;

        AuditTarget? target = string.IsNullOrEmpty(d.UserId)
            ? null
            : new AuditTarget("user", d.UserId!, d.Username);

        return ApiWrite(d, linked ? AuditAction.IdentityLink : AuditAction.IdentityUnlink,
            AuditSeverity.Warn, target, hostId, summary, meta);
    }

    /// <summary>Map a <c>service_connected</c> / <c>service_disconnected</c> event.</summary>
    public static AuditWrite FromServiceProvisioningEvent(
        ServiceProvisioningEventData d, string type, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        bool connected = string.Equals(type, ApiJournal.ServiceConnectedEvent, StringComparison.Ordinal);
        string display = string.IsNullOrEmpty(d.DisplayName) ? d.Leaf : d.DisplayName!;

        return ApiWrite(d,
            connected ? AuditAction.ServiceConnect : AuditAction.ServiceDisconnect,
            AuditSeverity.Info,
            new AuditTarget(AuditTargetKind.Leaf, d.Leaf, display),
            hostId,
            connected ? $"connected {display}" : $"disconnected {display}",
            meta: null);
    }

    /// <summary>Map a <c>service_config_changed</c> event.</summary>
    /// <remarks>⚠ Keys only. A leaf's configuration holds tokens and passwords.</remarks>
    public static AuditWrite FromServiceConfigEvent(ServiceConfigChangedEventData d, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        string display = string.IsNullOrEmpty(d.DisplayName) ? d.Leaf : d.DisplayName!;
        string[] keys = d.Keys ?? [];
        string keyList = keys.Length == 0 ? "" : $" ({string.Join(", ", keys)})";

        // A refusal is worth finding later as much as an apply, and the one that severed this API's own
        // link to the leaf is worth finding more than either.
        (string severity, string summary) = d.Outcome switch
        {
            "rolled_back" or "rejected" =>
                (AuditSeverity.Warn, $"rejected config change for {display}{keyList}"),
            "applied_unreachable" =>
                (AuditSeverity.Warn, $"configured {display}{keyList}, and can no longer reach it"),
            "unreachable" =>
                (AuditSeverity.Warn, $"could not reach {display} to change its configuration"),
            _ => (AuditSeverity.Info, $"configured {display}{keyList}"),
        };

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["keys"] = string.Join(",", keys),
        };
        if (!string.IsNullOrEmpty(d.Outcome)) meta["outcome"] = d.Outcome!;

        return ApiWrite(d, AuditAction.ServiceConfig, severity,
            new AuditTarget(AuditTargetKind.Leaf, d.Leaf, display), hostId, summary, meta);
    }

    /// <summary>Map a <c>service_restarted</c> event.</summary>
    /// <remarks>
    /// A restart systemd refused gets a row too — it is exactly the case nobody was watching a screen
    /// for, and a trail that only recorded the successes would be silent about it.
    /// </remarks>
    public static AuditWrite FromServiceRestartedEvent(ServiceRestartedEventData d, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        string display = string.IsNullOrEmpty(d.DisplayName) ? d.Leaf : d.DisplayName!;

        return ApiWrite(d, AuditAction.ServiceRestart,
            d.Ok ? AuditSeverity.Warn : AuditSeverity.Danger,
            new AuditTarget(AuditTargetKind.Host, d.Leaf, display),
            hostId,
            d.Ok ? $"restarted {display}" : $"tried to restart {display} and systemd refused",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unit"] = d.Unit,
                ["ok"] = d.Ok ? "true" : "false",
            });
    }

    /// <summary>Map a <c>file_written</c> event.</summary>
    /// <remarks>⚠ Path, size and hash — never the content.</remarks>
    public static AuditWrite FromFileWrittenEvent(FileWrittenEventData d, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = d.Path,
            ["sizeBytes"] = d.SizeBytes.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(d.Sha256)) meta["sha256"] = d.Sha256!;

        return ApiWrite(d, AuditAction.FileWrite, AuditSeverity.Info,
            new AuditTarget(AuditTargetKind.Server, d.InstanceName, d.InstanceName),
            hostId, $"edited file {d.Path} on {d.InstanceName}", meta, serverId: d.InstanceName);
    }

    /// <summary>Map a <c>backup_downloaded</c> event.</summary>
    public static AuditWrite FromBackupDownloadedEvent(BackupDownloadedEventData d, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = d.BackupId,
            ["sizeBytes"] = d.SizeBytes.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(d.Sha256)) meta["sha256"] = d.Sha256!;

        return ApiWrite(d, AuditAction.BackupDownload, AuditSeverity.Warn,
            new AuditTarget(AuditTargetKind.Server, d.InstanceName, d.InstanceName),
            hostId, $"downloaded a backup of {d.InstanceName}", meta, serverId: d.InstanceName);
    }

    /// <summary>
    /// Map a <c>command_failed</c> / <c>command_refused</c> / <c>command_cancelled</c> event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three severities for three different facts. A failure is <see cref="AuditSeverity.Danger"/> — a
    /// verb somebody asked for did not happen and something is wrong. A capacity refusal is
    /// <see cref="AuditSeverity.Warn"/>: nothing is wrong with the instance, the node was full, and
    /// filing it as a fault both blames the server and invites a retry certain to be refused again. A
    /// cancellation is <see cref="AuditSeverity.Info"/> — it is what the operator asked for.
    /// </para>
    /// <para>
    /// The engine's own error text rides in <c>meta</c> and never in the summary. The sentence says what
    /// did not happen, which is the same sentence whatever the engine's prose was, so a reworded kgsm
    /// message changes what a reader can dig into and not how the feed reads.
    /// </para>
    /// </remarks>
    public static AuditWrite FromCommandOutcomeEvent(CommandOutcomeEventData d, string type, string hostId)
    {
        ArgumentNullException.ThrowIfNull(d);

        string instance = Display(d.InstanceName);
        string word = CommandWord(d.Verb);

        (string action, string severity, string summary) = type switch
        {
            ApiJournal.CommandRefusedEvent => (
                AuditAction.CommandRefused, AuditSeverity.Warn,
                $"refused to {word} {instance} — not enough free memory on this node"),
            ApiJournal.CommandCancelledEvent => (
                AuditAction.CommandCancelled, AuditSeverity.Info,
                $"cancelled before it ran: {word} {instance}"),
            _ => (
                AuditAction.CommandFailed, AuditSeverity.Danger,
                $"could not {word} {instance}"),
        };

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(d.Verb)) meta["verb"] = d.Verb;
        if (!string.IsNullOrEmpty(d.JobId)) meta["jobId"] = d.JobId!;
        if (!string.IsNullOrEmpty(d.BatchId)) meta["batchId"] = d.BatchId!;
        if (d.ExitCode is { } code) meta["exitCode"] = code.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(d.Error)) meta["error"] = d.Error!;
        if (!string.IsNullOrEmpty(d.FromLibrary)) meta["fromLibrary"] = d.FromLibrary!;
        if (!string.IsNullOrEmpty(d.ToLibrary)) meta["toLibrary"] = d.ToLibrary!;

        return ApiWrite(d, action, severity,
            new AuditTarget(AuditTargetKind.Server, d.InstanceName, d.InstanceName),
            hostId, summary, meta, serverId: string.IsNullOrEmpty(d.InstanceName) ? null : d.InstanceName);
    }

    /// <summary>
    /// The verb as a sentence uses it.
    /// </summary>
    /// <remarks>
    /// A verb the set does not name is printed as it was recorded rather than replaced — a row about a
    /// command must be able to say which command, and a fallback that swallowed the name would leave the
    /// only record of a failure unable to say what failed.
    /// </remarks>
    private static string CommandWord(string? verb) => verb switch
    {
        CommandVerb.BackupCreate => "back up",
        CommandVerb.BackupRestore => "restore",
        { Length: > 0 } named => named,
        _ => "run a command on",
    };

    /// <summary>
    /// The row every event out of this API's own journal becomes, so none of them can disagree about
    /// where the timestamp, the actor or the origin come from.
    /// </summary>
    /// <remarks>
    /// All three ride on the ENVELOPE rather than the payload — the journal reader stamps them onto the
    /// typed data before a mapper sees it — which is what lets these rows carry the same provenance
    /// shape as every other producer's without each mapper re-deciding it.
    /// </remarks>
    private static AuditWrite ApiWrite(
        KgsmEventDataBase d, string action, string severity, AuditTarget? target, string hostId,
        string summary, Dictionary<string, string>? meta, string? serverId = null) =>
        new(
            Ts: d.Timestamp ?? DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(d.Origin),
            Actor: ParseActor(d.Actor),
            Action: action,
            Severity: severity,
            Target: target,
            ServerId: serverId,
            HostId: hostId,
            Summary: summary,
            Meta: meta is { Count: > 0 } ? meta : null);

    /// <summary>
    /// The display name for whoever acted, off the envelope's actor. Falls back to a bare "somebody"
    /// rather than to this API's own name: a row whose actor was not recorded must not read as though
    /// the panel did it on its own.
    /// </summary>
    private static string ActorName(string? actor) =>
        ParseActor(actor) is { Name.Length: > 0 } parsed ? parsed.Name : "somebody";
}
