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
    /// vocabulary is deferred to a future version — player-presence-contract.md §6).
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
    // Meta JSON column — no schema change (player-presence-contract.md §5).
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
}
