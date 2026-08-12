namespace TheKrystalShip.Api.Data;

/// <summary>
/// One notifiable fact waiting to go out in a summary, for a provider whose rule for it says
/// <c>digest</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a table rather than a list in memory because a digest is a promise.</b> Something held back
/// for hours and then lost to a restart was never delivered and never reported undelivered, which is the
/// one failure this whole channel is built to avoid. Rows survive a restart and go out late instead.
/// </para>
/// <para>
/// One row per <em>provider</em> per fact: the same event can be <c>every</c> on Slack and <c>digest</c>
/// on push, and each provider's rule is its own answer.
/// </para>
/// </remarks>
public sealed class NotificationDigestEntity
{
    public string Id { get; set; } = "";

    /// <summary>Which provider is holding it — its rule is what put the row here.</summary>
    public string Provider { get; set; } = "";

    public string CatalogId { get; set; } = "";

    /// <summary>The source action, so a renderer can phrase a nuance the catalog id flattens.</summary>
    public string Action { get; set; } = "";

    public string? ServerId { get; set; }

    public string Severity { get; set; } = "";

    /// <summary>The sentence the row already carried. A digest quotes it rather than rewriting it, so one
    /// fact never gets two wordings.</summary>
    public string Summary { get; set; } = "";

    /// <summary>When the fact happened — not when it will be sent. A digest names the hours it covers,
    /// and that is only true if the times are the events' own.</summary>
    public DateTimeOffset Ts { get; set; }
}
