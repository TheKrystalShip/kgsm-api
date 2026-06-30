namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The four leaves whose provisioning is runtime-flippable and whose config the API can target
/// (the leaf-runtime-provisioning/config feature) — <c>monitor</c>, <c>watchdog</c>, <c>assistant</c>,
/// <c>firewall</c>. These are exactly the operational leaves; <c>api</c> + <c>bot</c> are deliberately out
/// of scope (the API doesn't configure itself, and the bot is a separate Discord surface).
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

    /// <summary>The four provisionable + config-target leaf ids, in Services-board order.</summary>
    public static readonly IReadOnlyList<string> All = [Monitor, Watchdog, Assistant, Firewall];

    /// <summary>True when <paramref name="leafId"/> is one of the four runtime-provisionable leaves
    /// (everything else — <c>api</c>/<c>bot</c>/unknown — is not).</summary>
    public static bool IsProvisionable(string? leafId) =>
        leafId is Monitor or Watchdog or Assistant or Firewall;
}
