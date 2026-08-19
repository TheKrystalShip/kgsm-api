using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Actions;

namespace TheKrystalShip.Api.Services.Alerts;

/// <summary>
/// What each firing condition offers to do about itself — the map from an alert's producer to the buttons
/// its card carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most conditions offer nothing, and that is the design.</b> A verb belongs on a card only where one
/// press is an unambiguous instruction. A host running out of disk has no one-press remedy, and a button
/// that stopped the largest server would be this API guessing at a cause it has not established — the
/// operator opens the host and decides. Offering something there would make the two cases that <em>do</em>
/// have an answer indistinguishable from the ones that do not.
/// </para>
/// <para>
/// <b>The choice of verb is not made here.</b> It comes from <see cref="ConditionActions"/>, which the push
/// surface reads too — the same condition described on a phone and on a card must not be answered
/// differently. What this file decides is only which conditions this feed's three producers correspond to.
/// </para>
/// <para>
/// <b>Firing records only.</b> A resolved condition is a rear-view entry; there is nothing left to do about
/// it, and the panel draws no actions on one.
/// </para>
/// </remarks>
public static class AlertActionCatalog
{
    /// <summary>
    /// The offers for one firing condition, or empty when it offers nothing.
    /// </summary>
    /// <param name="source">The producer — an <see cref="AlertSource"/> value.</param>
    /// <param name="serverId">The affected server, or <see langword="null"/> for a host-wide condition.</param>
    /// <param name="escalated">The watchdog gave up (<c>Phase="failed"</c>). This is what separates a crash
    /// being retried from one nobody is retrying any more, and it inverts the verb.</param>
    public static IReadOnlyList<AlertAction> For(string source, string? serverId, bool escalated)
    {
        // Every offer below acts on a server. A condition that names none — a host metric — has no target
        // to act on, which is the same answer the metric case reaches anyway.
        if (string.IsNullOrEmpty(serverId)) return [];

        return source switch
        {
            // kgsm recorded that a newer build exists. Apply it.
            AlertSource.Engine => [new AlertAction(ConditionActions.UpdateAvailable, serverId)],

            // The watchdog's two crash states. Which verb helps depends entirely on whether the supervisor
            // is still trying — see ConditionActions for why each is the one that changes anything.
            AlertSource.Watchdog =>
                [new AlertAction(escalated ? ConditionActions.CrashLoop : ConditionActions.Crashed, serverId)],

            // A sustained threshold breach on one server. Deliberately nothing: the rule that fired names a
            // number, not a cause, and every verb available here — stop it, restart it — is a guess about
            // which. The card's job is to say which server and how long, and let a person look.
            _ => [],
        };
    }
}
