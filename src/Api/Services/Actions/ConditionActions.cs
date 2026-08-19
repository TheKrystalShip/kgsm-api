using TheKrystalShip.Api.Data;

namespace TheKrystalShip.Api.Services.Actions;

/// <summary>
/// Which operation a server condition deserves to be offered — the one answer, for every surface that
/// draws a button beside a condition.
/// </summary>
/// <remarks>
/// <para>
/// Two surfaces describe the same three conditions and must not answer differently: a push notification
/// (<see cref="Integrations.WebPush.PushActionCatalog"/>) and the alert feed
/// (<see cref="Alerts.AlertActionCatalog"/>). What each surface still owns is the <em>wording</em> — a
/// lock screen reads "Update now" because one line of text is all the context it has, and a card sitting
/// beside a server name reads "Update" because the rest is already on screen. Only the choice of verb is
/// shared, because only the choice of verb can be wrong.
/// </para>
/// <para>
/// The values are <see cref="PushActionKind"/> constants: that is the closed vocabulary of operations a
/// button can stand for, and it is a superset of the lifecycle verbs (it also names leaf, account and
/// moderation operations no alert describes yet). Nothing here is push-specific.
/// </para>
/// </remarks>
public static class ConditionActions
{
    /// <summary>
    /// A newer game build exists for a server. The one lifecycle verb a single button can mean exactly one
    /// thing by: apply the build the engine has already established is available.
    /// </summary>
    public const string UpdateAvailable = PushActionKind.ServerUpdate;

    /// <summary>
    /// A server crashed and the supervisor is bringing it back. <b>Stop, not Restart</b> — the watchdog is
    /// already restarting it, which is exactly what makes a crash reported repeatedly, so the only button
    /// that changes anything is the one that changes the desired state and lets the server stay down.
    /// </summary>
    public const string Crashed = PushActionKind.ServerStop;

    /// <summary>
    /// The supervisor exhausted its retries and gave up. <b>Start, not Stop</b> — the exact mirror of
    /// <see cref="Crashed"/>: the server is already down and staying down, so Stop asks for what already
    /// is. A crash cause that has since gone away — a full disk, a port somebody else was holding — makes
    /// the next attempt succeed, and if it does not, the supervisor gives up again and says so.
    /// </summary>
    public const string CrashLoop = PushActionKind.ServerStart;
}
