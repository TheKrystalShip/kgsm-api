using System.Text.Json.Serialization;

namespace TheKrystalShip.Api.Contracts;

/// <summary>
/// One KGSM leaf service on the host — its static identity + the live systemd state, plus the optional
/// deep-health probe where the api has one. The Services board (<c>GET /hosts/{id}/services</c>) renders one
/// of these per leaf. Honesty: an unmeasured field is omitted (<c>null</c>), never a fabricated default; a
/// not-installed leaf reports <c>state:"not-installed"</c> rather than being hidden.
/// </summary>
/// <param name="Id">Stable short id (<c>watchdog</c>) — the frontend key.</param>
/// <param name="DisplayName">Human label.</param>
/// <param name="Role">One-line description of what the leaf does.</param>
/// <param name="Unit">The backing systemd unit (<c>kgsm-watchdog.service</c>).</param>
/// <param name="State"><c>active|inactive|failed|activating|deactivating|reloading|maintenance|masked|
/// not-installed|unknown</c> — systemd's view (or honest unknown when unreadable).</param>
/// <param name="OnDemand">Socket-activated / idle-exiting (the firewall): <c>inactive</c> is its normal
/// resting state, so the UI renders it neutrally rather than as a fault.</param>
/// <param name="Provisioned">Runtime provisioning (connected-on-this-host) — <c>true</c>/<c>false</c> for the
/// four runtime-provisionable leaves (monitor/watchdog/assistant/firewall), <strong>null</strong> (omitted)
/// for <c>api</c>/<c>bot</c> where provisioning is not applicable. The UI shows a connect/disconnect toggle
/// only when this is present. Distinct from systemd <see cref="State"/> (a leaf can be connected yet its
/// unit inactive, or disconnected yet its unit running — provisioning is the SPA-facing capability flag).</param>
/// <param name="SubState">systemd's finer sub-state (<c>running|dead|exited|…</c>).</param>
/// <param name="Enabled">Starts on boot (null when N/A — static/masked — or unknown).</param>
/// <param name="Since">When the unit last became active (uptime is derived from it).</param>
/// <param name="MainPid">The main process pid (null when not running).</param>
/// <param name="MemoryBytes">systemd cgroup memory accounting (null when idle/unavailable).</param>
/// <param name="Health">The api's deep-health view where it probes this leaf (null when it has no probe —
/// distinct from a probed <c>down</c>/<c>unknown</c>).</param>
public sealed record LeafService(
    string Id,
    string DisplayName,
    string Role,
    string Unit,
    string State,
    bool OnDemand,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Provisioned,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SubState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Since,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MainPid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MemoryBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LeafServiceHealth? Health);

/// <summary>
/// The api's live deep-health view of a leaf, present only where the api actually probes it (the capability
/// model + the api itself). <see cref="Status"/> mirrors the capability vocabulary
/// (<c>operational|degraded|down|unknown</c>); a leaf with no probe carries a <c>null</c> health on the
/// <see cref="LeafService"/> rather than a fabricated one.
/// </summary>
public sealed record LeafServiceHealth(
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

/// <summary>The Services board payload — one row per configured leaf, in catalog order.</summary>
public sealed record ServicesSnapshot(IReadOnlyList<LeafService> Data);
