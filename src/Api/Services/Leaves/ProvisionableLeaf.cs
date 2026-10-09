namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The leaves whose provisioning is runtime-flippable and whose config the API can target (the
/// leaf-runtime-provisioning/config feature) — <c>monitor</c>, <c>watchdog</c>,
/// <c>firewall</c>, <c>scheduler</c>, <c>reactor</c>.
/// <para>
/// <b>The membership test is "does this API hold a link there is something to arm".</b> Each of these
/// gates a data flow of the API's own: a disconnect stops the metrics scrape, the capability probe, the
/// ports surface. That is what the Services board draws as the Link axis, and what makes its toggle a
/// control over something real rather than a preference.
/// </para>
/// <para>
/// Two leaves are deliberately out: <c>api</c> and <c>bot</c>, because this
/// API holds no client to them at all — it is itself, and the bot is a parallel Discord surface onto
/// kgsm-lib. Both report a <c>null</c> link, which the board renders as "not applicable" rather than as
/// "disconnected".
/// </para>
/// <para>
/// Socket activation is <b>not</b> the criterion, and reading it as one gets the firewall wrong: the
/// firewall is socket-activated and idle-exiting, and unpolled for exactly that reason
/// (<see cref="LeafHealthSource.None"/> — a 2s probe would defeat idle-exit). That is the health axis.
/// The firewall is still provisionable because the API holds a stored link that gates the ports surface.
/// </para>
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
    public const string Firewall = "firewall";
    public const string Scheduler = "scheduler";
    public const string Reactor = "reactor";

    /// <summary>The provisionable + config-target leaf ids, in Services-board order.</summary>
    public static readonly IReadOnlyList<string> All = [Monitor, Watchdog, Firewall, Scheduler, Reactor];

    /// <summary>True when <paramref name="leafId"/> is one of the runtime-provisionable leaves
    /// (everything else — <c>api</c>/<c>bot</c>/unknown — is not).</summary>
    public static bool IsProvisionable(string? leafId) =>
        leafId is Monitor or Watchdog or Firewall or Scheduler or Reactor;
}
