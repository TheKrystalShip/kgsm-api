namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// The per-server firewall/ports view (architecture.html §3·g, M6·b) — the cross-reference of what a
/// server <em>requires</em> against what the host firewall actually has open. A field on the
/// <see cref="Server"/> <strong>detail</strong> response only (<c>GET /servers/{id}</c>); omitted on the
/// list and on the <c>servers</c> stream (which stay the frozen M1·b shape). The fresh block is also
/// pushed on the dedicated <c>servers/{id}/network</c> WS topic after an <c>open_ports</c> command verifies.
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
/// polled <c>HostCapabilities</c> leaf; it is socket-activated + idle-exits).</param>
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
/// <param name="Open"><see langword="true"/> when the host firewall owns a rule covering this
/// <c>(port, proto)</c>; <see langword="false"/> when it does not (firewall answered); <see langword="null"/>
/// when the firewall could not answer — honest unknown, never a fabricated <c>false</c>.</param>
public sealed record RequiredPort(int Port, string Proto, bool? Open);

/// <summary>
/// The host-wide open-ports grid (architecture.html §3·g, M6·b) — the raw firewall listing for the
/// Diagnostics panel, a field on the <see cref="Host"/> <strong>detail</strong> response
/// (<c>GET /hosts/{id}</c>). The whole block is <see langword="null"/> when the firewall can't answer
/// (absent/unreachable/unknown — honest "not measurable now"); an <em>empty</em> <see cref="OpenPorts"/>
/// list means the firewall answered and owns no rules (the <c>Ok</c>-but-empty case, distinct from
/// <c>Unknown</c> which is null).
/// </summary>
public sealed record HostNetwork(IReadOnlyList<OpenPort> OpenPorts);

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
    public const string Down = "down";
    public const string Unknown = "unknown";
    public const string Unsupported = "unsupported";
    public const string Absent = "absent";
}
