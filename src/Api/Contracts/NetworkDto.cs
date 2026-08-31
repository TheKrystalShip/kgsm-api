namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The per-server firewall/ports view (architecture.html §3·g, M6·b) — the cross-reference of what a
/// server <em>requires</em> against what the host firewall actually has open. A field on the
/// <see cref="Server"/> <strong>detail</strong> response only (<c>GET /servers/{id}</c>); omitted on the
/// list and on the <c>servers</c> stream (which stay the frozen M1·b shape).
/// <para>
/// <strong>Honesty (the central M6·b call):</strong> <see cref="Required"/> is always knowable — it is the
/// server's own domain truth (kgsm <c>Instance.Ports</c>), independent of the firewall. Per-row
/// <see cref="RequiredPort.Open"/> is the host-firewall verdict (a rule is present), which goes
/// <see langword="null"/> — never a fabricated <c>false</c> — when the firewall can't answer
/// (<see cref="Firewall"/> ≠ <c>operational</c>). <see cref="Reachable"/> is <strong>reserved</strong>
/// (always <see langword="null"/>): §3·g asks for end-to-end reachability ("a rule can be applied while the
/// port stays blocked upstream — router NAT/ISP"), but the api has no upstream prober, so the strong name is
/// reserved for a real probe (e.g. a future UPnP/watchdog one) rather than overclaimed by the
/// rules-present aggregate. The frontend derives "all required rules open" from <see cref="Required"/> itself.
/// (The rename-not-redefine call, like M1·b's <c>cpuPctCore</c>-not-<c>cpu</c>.)
/// </para>
/// </summary>
/// <param name="Firewall">The block-level firewall availability for this probe
/// (<see cref="FirewallAvailability"/>) — the single liveness signal (the firewall is deliberately NOT a
/// polled <c>HostCapabilities</c> leaf; it is socket-activated + idle-exits). <strong>Clients MUST read
/// this alongside <c>open</c>:</strong> when it is <see cref="FirewallAvailability.Inactive"/>, every
/// <c>open:true</c> means "reachable because the host firewall is OFF (nothing is filtering)" — the opposite
/// security posture from <c>open:true</c> under <see cref="FirewallAvailability.Operational"/> ("allowed by
/// a rule"). A UI that paints a green check on <c>open:true</c> without consulting this would render
/// "all clear" over "no firewall at all".</param>
/// <param name="Required">The server's required ports, expanded one row per port from
/// <c>Instance.Ports</c> — always present (domain truth), even when the firewall is absent.</param>
/// <param name="Reachable">Reserved — always <see langword="null"/> (no upstream prober; see the type remarks).</param>
public sealed record ServerNetwork(
    string Firewall,
    IReadOnlyList<RequiredPort> Required,
    bool? Reachable);

/// <summary>One required port and its host-firewall verdict (M6·b).</summary>
/// <param name="Port">The single port number (ranges are expanded one row per port).</param>
/// <param name="Proto">Transport protocol — <c>"tcp"</c> or <c>"udp"</c> (lower-cased).</param>
/// <param name="Open"><see langword="true"/> when the port is reachable at the host firewall — either a
/// rule allows it (firewall <c>operational</c>) OR the firewall is <c>inactive</c> (nothing filters, so all
/// ports are open); <see langword="false"/> when the firewall is operational and owns no covering rule
/// (default-deny); <see langword="null"/> when the firewall could not answer (down/unknown/unsupported/absent)
/// — honest unknown, never a fabricated <c>false</c>. Always read with the block-level <c>firewall</c> status
/// to tell "open because allowed" from "open because the firewall is off".</param>
public sealed record RequiredPort(int Port, string Proto, bool? Open);

/// <summary>
/// The host-wide open-ports grid (architecture.html §3·g, M6·b) — the raw firewall listing for the
/// Diagnostics panel, a field on the <see cref="Host"/> <strong>detail</strong> response
/// (<c>GET /hosts/{id}</c>). The whole block is <see langword="null"/> when the firewall can't answer
/// (absent/unreachable/unknown — honest "not measurable now").
/// <para>
/// <strong>Read <see cref="Firewall"/> to interpret <see cref="OpenPorts"/>:</strong> when
/// <see cref="Firewall"/> is <see cref="FirewallAvailability.Operational"/>, an <em>empty</em>
/// <see cref="OpenPorts"/> means "the firewall is enforcing and owns no rules" (nothing open); but when it
/// is <see cref="FirewallAvailability.Inactive"/>, an empty <see cref="OpenPorts"/> means the OPPOSITE —
/// the firewall is OFF, so <em>every</em> port is open/unfiltered (the grid is empty only because an
/// inactive ufw enumerates no active rules). Never read the empty grid as "nothing open" without the status.
/// </para>
/// </summary>
public sealed record HostNetwork(string Firewall, IReadOnlyList<OpenPort> OpenPorts);

/// <summary>One host-firewall rule, expanded one row per port (M6·b).</summary>
/// <param name="Port">The single port number.</param>
/// <param name="Proto">Transport protocol — <c>"tcp"</c> or <c>"udp"</c>.</param>
/// <param name="App">The game/blueprint id this instance was installed from, joined from the kgsm roster,
/// or <see langword="null"/> when the owning instance isn't in the roster (never guessed).</param>
/// <param name="Server">The instance name that owns the rule (the firewall's own data).</param>
public sealed record OpenPort(int Port, string Proto, string? App, string Server);

/// <summary>
/// The block-level firewall availability (M6·b) — the single honest liveness signal for the ports
/// surface, reported per-probe (the firewall is not a polled leaf). Maps from the kgsm-lib
/// <c>IFirewallService</c> outcome: <see cref="Operational"/> = a successful <c>ListOwnedAsync</c>;
/// <see cref="Down"/> = unreachable (<c>FirewallException</c>/timeout); <see cref="Unknown"/> = the backend
/// can't enumerate (the honest <c>ListOwnedAsync</c> <c>Unknown</c>, never collapsed to empty);
/// <see cref="Unsupported"/> = the backend doesn't support listing; <see cref="Absent"/> = not provisioned.
/// </summary>
public static class FirewallAvailability
{
    public const string Operational = "operational";
    /// <summary>The firewall authority is reachable but NOT enforcing (e.g. ufw inactive) — it filters
    /// nothing, so every port is open/unfiltered. Maps from kgsm-lib's <c>FirewallEnforcement.Inactive</c>
    /// (Firewall.Contracts 1.1.0). With this status, <c>open:true</c> means "reachable because the firewall
    /// is OFF", not "allowed" — a distinct security posture the client must surface (see <see cref="ServerNetwork"/>).</summary>
    public const string Inactive = "inactive";
    public const string Down = "down";
    public const string Unknown = "unknown";
    public const string Unsupported = "unsupported";
    public const string Absent = "absent";
}

/// <summary>One port this host is listening on.</summary>
/// <param name="Port">The port number.</param>
/// <param name="Protocol">The protocol it listens on — <c>tcp</c> or <c>udp</c>.</param>
/// <param name="Process">The process holding the socket, or <c>null</c> when the scan could not
/// attribute it. Null is "who holds it is unknown", never a placeholder — the port itself is measured
/// either way.</param>
/// <param name="Instance">The instance configured for this port, when one is. Joined from the engine's
/// own instance list, never guessed from the process name, which is a binary's name and not a
/// server's.</param>
public sealed record HostPortDto(int Port, string Protocol, string? Process, string? Instance);

/// <summary>Two claimants on one port, as the engine finds them.</summary>
/// <param name="Port">The contested port.</param>
/// <param name="Protocol">The protocol the contest is on.</param>
/// <param name="Instance">The instance whose configuration claims the port.</param>
/// <param name="Other">The other claimant — another instance, or a process outside KGSM.</param>
/// <param name="OtherIsInstance">Whether <paramref name="Other"/> names another instance rather than an
/// outside process. The two read alike and are fixed by completely different actions, so the
/// distinction travels with the finding instead of being inferred from what the name looks like.</param>
public sealed record PortConflictDto(
    int Port, string Protocol, string Instance, string Other, bool OtherIsInstance);

/// <summary>
/// What is bound on the host and where two claimants collide (<c>GET /hosts/{id}/ports</c>).
/// </summary>
/// <param name="State"><c>available</c> or <c>unavailable</c> for the listening-port scan.</param>
/// <param name="UsedPorts">What is listening, empty when the scan could not be read.</param>
/// <param name="ConflictState"><c>available</c> or <c>unavailable</c> for the conflict scan.</param>
/// <param name="Conflicts">The contested ports, empty when the scan could not be read.</param>
/// <remarks>
/// The two axes carry their own state because they are two scans: one can answer while the other
/// cannot, and an unread conflict scan reported as an empty list is invisible — no conflicts is the
/// ordinary answer, so a failure collapsing into it looks exactly like a healthy host.
/// </remarks>
public sealed record HostPortsDto(
    string State,
    IReadOnlyList<HostPortDto> UsedPorts,
    string ConflictState,
    IReadOnlyList<PortConflictDto> Conflicts);
