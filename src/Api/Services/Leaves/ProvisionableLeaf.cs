namespace TheKrystalShip.Api.Services.Leaves;

/// <summary>
/// The leaves whose provisioning is runtime-flippable and whose config the API can target (the
/// leaf-runtime-provisioning/config feature) — <c>monitor</c>, <c>watchdog</c>, <c>assistant</c>,
/// <c>firewall</c>, <c>scheduler</c>, <c>reactor</c>.
/// <para>
/// <b>The membership test is "does this API hold a link there is something to arm".</b> Each of these
/// gates a data flow of the API's own: a disconnect stops the metrics scrape, the capability probe, the
/// ports surface. That is what the Services board draws as the Link axis, and what makes its toggle a
/// control over something real rather than a preference.
/// </para>
/// <para>
/// Three leaves are deliberately out, for two different reasons. <c>api</c> and <c>bot</c> because this
/// API holds no client to them at all — it is itself, and the bot is a parallel Discord surface onto
/// kgsm-lib. <c>speech</c> because its presence is <b>measured, not stored</b>: the socket file answers
/// whether the leaf is installed here (<see cref="SpeechLeafClient"/>), the API reads it on a page view
/// and nothing else, and every path that actually uses the engine — the assistant service, the bot, a
/// browser recording a voice note — reaches it directly, not through here. A toggle would arm nothing,
/// while appearing to be the switch that turns voice off. All three report a <c>null</c> link, which the
/// board renders as "not applicable" rather than as "disconnected".
/// </para>
/// <para>
/// Socket activation is <b>not</b> the criterion, and reading it as one gets the firewall wrong: the
/// firewall and the speech engine are both socket-activated and idle-exiting, and both are unpolled for
/// exactly that reason (<see cref="LeafHealthSource.None"/> — a 2s probe would defeat idle-exit, and
/// probing speech would load a gigabyte of models to ask whether it is alive). That is the health axis.
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
