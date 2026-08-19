namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The leaves whose provisioning is runtime-flippable and whose config the API can target (the
/// leaf-runtime-provisioning/config feature) — <c>monitor</c>, <c>watchdog</c>, <c>assistant</c>,
/// <c>firewall</c>, <c>scheduler</c>, <c>reactor</c>. These are exactly the leaves this API holds a link
/// to; <c>api</c> + <c>bot</c> + <c>speech</c> are deliberately out of scope (the API doesn't configure
/// itself, the bot is a separate Discord surface, and the speech engine idle-exits so connecting to it is
/// what would start it).
/// <para>
/// Ids are the <see cref="LeafCatalog"/> ids (the registry/Services-board key). The capability the SPA gates
/// on uses a different vocabulary (the monitor reports the <c>metrics</c> capability), so
/// <see cref="CapabilityToLeaf"/> bridges the two for the <see cref="LeafHealthMonitor"/>.
/// </para>
/// </summary>
public static class ProvisionableLeaf
{
    public const string Monitor = "monitor";
    public const string Watchdog = "watchdog";
    public const string Assistant = "assistant";
    public const string Firewall = "firewall";
    public const string Scheduler = "scheduler";
    public const string Reactor = "reactor";

    /// <summary>The provisionable + config-target leaf ids, in Services-board order.</summary>
    public static readonly IReadOnlyList<string> All = [Monitor, Watchdog, Assistant, Firewall, Scheduler, Reactor];

    /// <summary>True when <paramref name="leafId"/> is one of the runtime-provisionable leaves
    /// (everything else — <c>api</c>/<c>bot</c>/<c>speech</c>/unknown — is not).</summary>
    public static bool IsProvisionable(string? leafId) =>
        leafId is Monitor or Watchdog or Assistant or Firewall or Scheduler or Reactor;
}
