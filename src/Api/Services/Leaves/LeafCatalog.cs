namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// Where the live-health row for a leaf comes from on the Services board. The api already probes a few
/// leaves (the capability model — <see cref="LeafHealthMonitor"/>); the rest have no <c>/health</c> the api
/// polls, so their deep-health is honest <c>null</c> (systemd liveness is still shown), never fabricated.
/// </summary>
public enum LeafHealthSource
{
    /// <summary>No deep-health probe — only systemd liveness is known. The firewall is socket-activated and
    /// idle-exits (deliberately NOT polled — see <see cref="ApiOptions.FirewallSocketPath"/>); the bot is a
    /// separate Discord surface. Neither serves a <c>/health</c> the api watches.</summary>
    None,

    /// <summary>This API itself — reachable by definition whenever it answers the request.</summary>
    SelfApi,

    /// <summary>The monitor's metrics capability (<see cref="LeafHealthMonitor"/> → monitor <c>/health</c>).</summary>
    Metrics,

    /// <summary>The assistant's capability (<see cref="LeafHealthMonitor"/> → assistant <c>/health</c>).</summary>
    Assistant,

    /// <summary>The watchdog's readiness (<see cref="LeafHealthMonitor"/> → kgsm-lib <c>IWatchdogClient.IsReadyAsync</c>).</summary>
    Watchdog,
}

/// <summary>
/// One KGSM leaf service the host runs — the static identity the Services board renders alongside its live
/// systemd state + (where available) the api's deep-health probe.
/// </summary>
/// <param name="Id">Stable short id (<c>watchdog</c>) — the frontend key and the log-source id.</param>
/// <param name="Unit">The systemd unit that carries it (<c>kgsm-watchdog.service</c>).</param>
/// <param name="DisplayName">Human label for the card.</param>
/// <param name="Role">One-line description of what the leaf does.</param>
/// <param name="OnDemand">True for a socket-activated / idle-exiting unit (the firewall): an <c>inactive</c>
/// state is its NORMAL resting state, not a fault — the UI renders it neutrally rather than as "stopped".</param>
/// <param name="Health">Which capability probe (if any) supplies this leaf's deep-health row.</param>
public sealed record LeafDescriptor(
    string Id,
    string Unit,
    string DisplayName,
    string Role,
    bool OnDemand,
    LeafHealthSource Health);

/// <summary>
/// The canonical KGSM leaf catalog — the SINGLE source of truth for "what services make up a host". Both the
/// Services board (<c>GET /hosts/{id}/services</c>) and the host-log source map (<see cref="ApiOptions.LogSources"/>,
/// derived from this) read it, so the two surfaces can never drift on which units a host comprises. Order is
/// the order both surfaces present the leaves.
/// </summary>
public static class LeafCatalog
{
    public static readonly IReadOnlyList<LeafDescriptor> Default =
    [
        new("watchdog", "kgsm-watchdog.service", "Watchdog",
            "Resident supervisor — owns kgsm.slice, native lifecycle & crash-restart", false, LeafHealthSource.Watchdog),
        new("monitor", "kgsm-monitor.service", "Monitor",
            "Host & per-server resource metrics", false, LeafHealthSource.Metrics),
        new("assistant", "kgsm-assistant-service.service", "Assistant",
            "LLM assistant — chat & tool-calling turns", false, LeafHealthSource.Assistant),
        new("firewall", "kgsm-firewall.service", "Firewall",
            "Host firewall authority — opens & closes server ports", true, LeafHealthSource.None),
        new("api", "kgsm-api.service", "Control Panel API",
            "The aggregator API serving this panel (this service)", false, LeafHealthSource.SelfApi),
        new("bot", "kgsm-bot.service", "Discord bot",
            "Discord control surface onto KGSM", false, LeafHealthSource.None),
    ];
}
