using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Api.Services.Availability;

/// <summary>
/// Since when each instance has been running an out-of-date build — the age behind
/// <see cref="Contracts.Server.UpdateAvailableSince"/>.
/// </summary>
/// <remarks>
/// <para><b>Why this is not a column.</b> The engine already records the moment it noticed
/// (<c>instance_update_available</c>) and the moment the version moved (<c>instance_version_updated</c>).
/// A stored "first seen out of date" would be a second copy of a fact the journal already holds, free to
/// disagree with it after any restart, wipe or manual update. This reads the journal instead.</para>
/// <para><b>Why it is cached.</b> The roster is rebuilt on every read and on a 60s pump; walking the
/// journal each time would put a disk scan behind <c>GET /servers</c>. The figure is a multi-day age, so
/// a refresh cadence measured in minutes costs it nothing — and the answer is only ever <em>shown</em>
/// for an instance the roster independently reports as out of date, so a stale entry cannot surface as a
/// claim about a server that is already up to date.</para>
/// </remarks>
public sealed class UpdateLagIndex(
    IServiceProvider services,
    ILogger<UpdateLagIndex> logger) : BackgroundService
{
    /// <summary>How often the journal is re-walked. An age in days does not need a tighter loop.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back a walk reads. An instance out of date for longer than this reports the floor rather
    /// than a longer age — understating a known-large gap, never inventing one.
    /// </summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(180);

    private const int PageSize = 500;
    private const int MaxPages = 8;

    private volatile IReadOnlyDictionary<string, DateTimeOffset> _since =
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

    /// <summary>
    /// When <paramref name="instanceId"/> was first seen to be out of date, or null when the journal
    /// records no such observation still standing. Never fabricated: an instance whose notice predates
    /// the walk's <see cref="Lookback"/>, or whose engine emits no such event, is honestly unknown.
    /// </summary>
    public DateTimeOffset? Since(string instanceId) =>
        _since.TryGetValue(instanceId, out DateTimeOffset ts) ? ts : null;

    /// <summary>
    /// A snapshot-shaped accessor for the roster join, matching the <c>onlinePlayers</c> pattern in
    /// <see cref="Aggregation.ServerAggregator"/>: a plain function, so the builder stays free of this
    /// service and a pump can compose the identical rule.
    /// </summary>
    public Func<string, DateTimeOffset?> Lookup => Since;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // An unreadable journal leaves the last good map in place and the field honestly absent
                // for anything new — it never turns into a fabricated "just now".
                logger.LogWarning(ex, "Update-lag walk failed; keeping the previous index.");
            }

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Rebuild the map: for each instance, the OLDEST <c>instance_update_available</c> that no
    /// <c>instance_version_updated</c> has since cleared.
    /// </summary>
    /// <remarks>
    /// Oldest rather than newest because the scheduler re-emits the notice on every sweep that still
    /// finds the instance behind — taking the newest would reset the age every few minutes and report a
    /// server that has been stale for a week as stale for a moment, which is the exact opposite of what
    /// the figure is for.
    /// </remarks>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        // kgsm-lib's services are registered only where the engine is provisioned, and they are scoped —
        // the same reason AuditController resolves its reader per request rather than injecting it.
        using IServiceScope scope = services.CreateScope();
        IEventJournalHistory? journal = scope.ServiceProvider.GetService<IEventJournalHistory>();
        if (journal is null) return;

        DateTimeOffset from = DateTimeOffset.UtcNow - Lookback;
        long fromMs = from.ToUnixTimeMilliseconds();

        List<EventHistoryEntry> notices = await ReadAllAsync(journal, "instance_update_available", fromMs, ct)
            .ConfigureAwait(false);
        List<EventHistoryEntry> updates = await ReadAllAsync(journal, "instance_version_updated", fromMs, ct)
            .ConfigureAwait(false);

        _since = Select(notices, updates);
    }

    /// <summary>
    /// Pick each instance's still-standing notice: the oldest <paramref name="notices"/> entry that no
    /// entry in <paramref name="updates"/> has since superseded.
    /// </summary>
    internal static Dictionary<string, DateTimeOffset> Select(
        IReadOnlyList<EventHistoryEntry> notices, IReadOnlyList<EventHistoryEntry> updates)
    {
        // The last time each instance's version actually moved. A notice at or before it describes a gap
        // that has since been closed and says nothing about now.
        Dictionary<string, DateTimeOffset> lastUpdate = new(StringComparer.Ordinal);
        foreach (EventHistoryEntry e in updates)
            if (e.Instance is { } inst && (!lastUpdate.TryGetValue(inst, out DateTimeOffset prev) || e.Ts > prev))
                lastUpdate[inst] = e.Ts;

        Dictionary<string, DateTimeOffset> since = new(StringComparer.Ordinal);
        foreach (EventHistoryEntry e in notices)
        {
            if (e.Instance is not { } inst) continue;
            if (lastUpdate.TryGetValue(inst, out DateTimeOffset updated) && e.Ts <= updated) continue;
            if (!since.TryGetValue(inst, out DateTimeOffset first) || e.Ts < first) since[inst] = e.Ts;
        }

        return since;
    }

    private static async Task<List<EventHistoryEntry>> ReadAllAsync(
        IEventJournalHistory journal, string type, long fromMs, CancellationToken ct)
    {
        List<EventHistoryEntry> all = [];
        long? beforeTs = null;
        string? beforeId = null;

        for (int page = 0; page < MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            EventHistoryPage p = await journal.QueryAsync(new EventHistoryQuery
            {
                Type = type,
                SinceMs = fromMs,
                BeforeTsMs = beforeTs,
                BeforeId = beforeId,
                Limit = PageSize,
            }, ct).ConfigureAwait(false);

            if (!p.JournalReadable) break;
            all.AddRange(p.Events);
            if (p.NextCursorTsMs is null) break;
            beforeTs = p.NextCursorTsMs;
            beforeId = p.NextCursorId;
        }

        return all;
    }
}
