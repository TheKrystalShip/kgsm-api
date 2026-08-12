namespace TheKrystalShip.Api.Services.Integrations;

/// <summary>
/// The one sentence at the top of a summary, shared by every provider that renders one.
/// </summary>
/// <remarks>
/// Shared because the alternative is each provider inventing its own count of the same batch, and two
/// surfaces disagreeing about how many things happened is exactly the sort of small lie this codebase
/// spends effort avoiding. The <em>lines</em> below it stay each provider's business, since a lock screen
/// and a Slack channel have different room.
/// </remarks>
public static class NotificationDigest
{
    /// <summary>
    /// "3 servers have updates" when a batch is all one thing, "5 things happened" when it is not.
    /// </summary>
    /// <remarks>
    /// The specific phrasing only appears where it is <b>true of every event in the batch</b>. A mixed
    /// batch gets the flat count, because a headline naming one kind of event and a body listing four
    /// others is a headline that misleads on the surface where a lot of people stop reading.
    /// </remarks>
    public static string Headline(IReadOnlyList<NotificationEvent> events)
    {
        int n = events.Count;
        string catalogId = events[0].CatalogId;
        bool uniform = events.All(e => string.Equals(e.CatalogId, catalogId, StringComparison.Ordinal));

        if (!uniform) return $"{n} thing{(n == 1 ? "" : "s")} happened while you were away";

        // The count is of events, and for the server-scoped ones that is a count of servers only because
        // the coalescing upstream already collapsed repeats per server.
        return catalogId switch
        {
            "update_available" => $"{n} server{(n == 1 ? "" : "s")} {(n == 1 ? "has" : "have")} an update",
            "crash" => $"{n} crash{(n == 1 ? "" : "es")}",
            "crash_loop" => $"{n} server{(n == 1 ? "" : "s")} the watchdog gave up on",
            "offline" => $"{n} server{(n == 1 ? "" : "s")} went offline",
            "online" => $"{n} server{(n == 1 ? "" : "s")} came online",
            "backup" => $"{n} backup{(n == 1 ? "" : "s")}",
            "installed" => $"{n} server{(n == 1 ? "" : "s")} installed",
            "update" => $"{n} server{(n == 1 ? "" : "s")} updated",
            "player_join" => $"{n} player{(n == 1 ? "" : "s")} joined",
            "server_empty" => $"{n} server{(n == 1 ? "" : "s")} sitting empty",
            "threshold_breach" => $"{n} threshold{(n == 1 ? "" : "s")} crossed",
            "threshold_clear" => $"{n} threshold{(n == 1 ? "" : "s")} recovered",
            "leaf_down" => $"{n} service{(n == 1 ? "" : "s")} went down",
            "leaf_up" => $"{n} service{(n == 1 ? "" : "s")} came back",
            "awaiting_approval" => $"{n} {(n == 1 ? "person" : "people")} waiting to be let in",
            _ => $"{n} thing{(n == 1 ? "" : "s")} happened while you were away",
        };
    }

    /// <summary>
    /// The servers a batch of <c>update_available</c> facts names, or empty when the batch is not
    /// uniformly that — the one case where a single tap on a summary is an unambiguous instruction.
    /// </summary>
    /// <remarks>
    /// De-duplicated and ordered, because the batch may hold two facts about one server if it sat long
    /// enough, and a button offering to update six things that are four things would be lying about its
    /// own scope.
    /// </remarks>
    public static IReadOnlyList<string> UpdatableServers(IReadOnlyList<NotificationEvent> events)
    {
        if (!events.All(e => e.CatalogId == "update_available" && !string.IsNullOrEmpty(e.ServerId)))
            return [];

        return events.Select(e => e.ServerId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
}
