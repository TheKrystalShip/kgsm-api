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
    /// Map a kgsm <c>instance_ports_opened</c> event (the CLI-path firewall echo — kgsm bash opened
    /// the host-firewall ports on a confirmed success) to a <c>network.ports.open</c> row, recording
    /// the opened ports in <c>meta</c> in the canonical range-preserving form. The api-issued
    /// <c>open_ports</c> command writes this action directly at M6·b (no kgsm echo exists), so this
    /// mapper covers only the engine-sourced opens.
    /// </summary>
    public static AuditWrite FromPortsOpenedEvent(InstancePortsOpenedData d, string hostId) =>
        PortsWrite(d, hostId, AuditAction.NetworkPortsOpen, "opened", d.Ports);

    /// <summary>
    /// Map a kgsm <c>instance_ports_closed</c> event (the CLI-path firewall echo — kgsm bash removed
    /// the host-firewall ports on a confirmed success, via uninstall or a standalone firewall-disable)
    /// to a <c>network.ports.close</c> row. Recording closes keeps the trail symmetric — a disable that
    /// isn't part of an uninstall would otherwise leave an opened-never-closed gap. There is no
    /// api-issued close command (§3·g is open-only), so this action is cleanly CLI-echo-only.
    /// </summary>
    public static AuditWrite FromPortsClosedEvent(InstancePortsClosedData d, string hostId) =>
        PortsWrite(d, hostId, AuditAction.NetworkPortsClose, "closed", d.Ports);

    /// <summary>
    /// Build the <see cref="AuditWrite"/> for the API-issued <c>open_ports</c> command (M6·b) — a
    /// <strong>direct</strong> write, the <c>auth.*</c> case: the api opens the ports through kgsm-lib's
    /// <c>IFirewallService</c>, which runs no kgsm command and emits no event, so there is no echo to read
    /// and no double-write risk (the CLI path's <c>instance_ports_opened</c> echo is disjoint —
    /// <see cref="FromPortsOpenedEvent"/>). Provenance is the bearer <paramref name="actor"/> + the
    /// caller-declared <paramref name="origin"/>, parsed/normalized exactly like an event's. <c>meta</c>
    /// carries the opened ports <em>and</em> the <paramref name="jobId"/> — the job↔audit correlation the
    /// M5 echo path could not provide (no id round-trips the stateless engine), now populatable because the
    /// api owns both the job and this append (the alert↔audit <c>resolution.actionId</c> bridge for M6·a).
    /// </summary>
    /// <param name="enforced"><see langword="true"/> when the firewall is enforcing (the rule is live —
    /// "opened"); <see langword="false"/> when it was staged on an INACTIVE firewall (the
    /// <c>applied-inactive</c> outcome — the rule persists and enforces on the operator's next
    /// <c>ufw enable</c>, and the port is open meanwhile). The audit row must say "staged", not "opened",
    /// when nothing is enforcing — recording an enforced open that didn't happen would be the very lie this
    /// work removes.</param>
    public static AuditWrite FromPortsOpenedCommand(
        string serverId, IReadOnlyList<PortMapping> ports, string? actor, string? origin, string hostId,
        string jobId, bool enforced = true)
    {
        var meta = new Dictionary<string, string> { ["jobId"] = jobId };
        string formatted = FormatPorts(ports);
        if (!string.IsNullOrEmpty(formatted)) meta["ports"] = formatted;
        if (!enforced) meta["enforced"] = "false"; // staged on an inactive firewall, not yet enforcing

        return new AuditWrite(
            Ts: DateTimeOffset.UtcNow,
            Origin: NormalizeOrigin(origin),
            Actor: ParseActor(actor),
            Action: AuditAction.NetworkPortsOpen,
            Severity: AuditSeverity.Info,
            Target: new AuditTarget(AuditTargetKind.Server, serverId, serverId),
            ServerId: serverId,
            HostId: hostId,
            Summary: enforced
                ? $"opened firewall ports for {Display(serverId)}"
                : $"staged firewall ports for {Display(serverId)} (firewall inactive — enforces on enable)",
            Meta: meta);
    }

    /// <summary>
    /// Map a kgsm <c>instance_player_joined</c> event to a <c>player.join</c> row at
    /// <see cref="AuditSeverity.Info"/>. For our container images this is forwarded by the watchdog from
    /// the in-image detection shim (provenance <c>system</c>/<c>system</c> off the envelope, handled
    /// unchanged by <see cref="ParseActor"/>/<see cref="NormalizeOrigin"/>). The player identity
    /// (<c>playerId</c>/<c>playerName</c>, either nullable) rides in <c>meta</c>; the row is scoped to the
    /// server (no player target kind), mirroring the crash mapper. Never fabricates the missing half.
    /// </summary>
    public static AuditWrite FromPlayerJoinedEvent(InstancePlayerJoinedData d, string hostId) =>
        PlayerWrite(d, hostId, AuditAction.PlayerJoin, "joined", d.PlayerId, d.PlayerName);

    /// <summary>
    /// Map a kgsm <c>instance_player_left</c> event to a <c>player.leave</c> row — the leave counterpart of
    /// <see cref="FromPlayerJoinedEvent"/>, identical provenance/identity rules.
    /// </summary>
    public static AuditWrite FromPlayerLeftEvent(InstancePlayerLeftData d, string hostId) =>
        PlayerWrite(d, hostId, AuditAction.PlayerLeave, "left", d.PlayerId, d.PlayerName);

    // Join/left differ only in action + summary verb — build the row once. The summary names the player
    // by display name, falling back to the stable id, then a generic label (never fabricates an identity;
    // at-least-one-non-null is the emitting shim's guarantee, this is defensive).
    private static AuditWrite PlayerWrite(
        EventDataBase d, string hostId, string action, string verb, string? playerId, string? playerName)
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
            Meta: PlayerMeta(playerId, playerName));
    }

    // Meta off a player event (id/name, either nullable); empties dropped, null when neither is present —
    // never store "". The honest identity, never fabricated.
    private static IReadOnlyDictionary<string, string>? PlayerMeta(string? id, string? name)
    {
        Dictionary<string, string>? meta = null;
        if (!string.IsNullOrEmpty(id)) (meta ??= [])["playerId"] = id!;
        if (!string.IsNullOrEmpty(name)) (meta ??= [])["playerName"] = name!;
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
}
