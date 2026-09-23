namespace TheKrystalShip.Api.Data;

/// <summary>
/// A session the cluster's auth anchor minted that somebody has since ended.
/// </summary>
/// <remarks>
/// A session is accepted on this node because its signature verifies against the key the anchor
/// publishes, so nothing about accepting one is stored here. What has to be stored is the one fact a
/// signature cannot carry: that the session is over. The bus delivers it, and this row is what stops
/// the bearer working for the rest of its life.
/// </remarks>
public sealed class EndedSessionEntity
{
    /// <summary>The session, as the <c>sid</c> claim names it. Primary key.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>When this node learned the session was over. UTC.</summary>
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>
    /// When the row may be swept — the longest a bearer for the session could still be presented, not
    /// when the session died. UTC.
    /// </summary>
    public DateTimeOffset Until { get; set; }
}
