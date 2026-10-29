using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// A cluster member's label by its member id, for the audit rows that name another node.
/// </summary>
/// <remarks>
/// <para>
/// The label is what the roster shows a person: the member's nickname, or its id when it has none. Rows
/// store the id, which never changes, and are worded at read time, so a renamed member reads by its new
/// label on every row it appears on.
/// </para>
/// <para>
/// <b>Synchronous, from a copy refreshed in the background.</b> The shaping that asks runs on paths that
/// cannot wait on the cluster store, so a stale or empty copy answers at once and a refresh is started; a
/// member not in the copy reads as <see langword="null"/>, and the row falls back to the id.
/// </para>
/// </remarks>
public sealed class MemberLabels(MembersStore members, ILogger<MemberLabels> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private IReadOnlyDictionary<string, string> _labels = new Dictionary<string, string>(StringComparer.Ordinal);
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private int _refreshing;

    /// <summary>The label of <paramref name="memberId"/>, or <see langword="null"/> when it is not known.</summary>
    public string? Label(string memberId)
    {
        if (DateTimeOffset.UtcNow - _readAt > MaxAge)
            _ = RefreshAsync();

        return _labels.TryGetValue(memberId, out string? label) ? label : null;
    }

    /// <summary>Reads the roster into the copy. One refresh runs at a time.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            IReadOnlyList<MemberRow> rows = await members.ListAsync(ct).ConfigureAwait(false);
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MemberRow row in rows)
                labels[row.MemberId] = string.IsNullOrWhiteSpace(row.Nickname) ? row.MemberId : row.Nickname;

            _labels = labels;
            _readAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "member labels could not be read; rows name members by id");
            _readAt = DateTimeOffset.UtcNow;
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }
}
