using System.Net.Http.Headers;

using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Takes this node's first full copy of the cluster's accounts from the member that holds them.
/// </summary>
/// <remarks>
/// <para>
/// The stream alone is not enough. A node joins on Tuesday and the changes that happened on Monday
/// were fanned out to members that existed then — so without this it holds only what has changed
/// since it arrived, and resolves everybody else as a stranger. One snapshot, then the stream.
/// </para>
/// <para>
/// <b>Ordering with the stream needs no coordination.</b> Both paths carry the same per-account
/// version and the replica refuses anything not newer, so a change landing while the snapshot is
/// being applied is safe whichever order the two arrive in. That is what makes "snapshot, then
/// follow" a sequence rather than a handover.
/// </para>
/// <para>
/// <b>It keeps trying until it succeeds, and then stops.</b> The holder may be unreachable, may not
/// be assigned yet, or may be this node itself; none of those is an error, and none is permanent.
/// Once a snapshot has been taken from a given holder this worker is done — the stream carries
/// everything after it, and re-reading the whole set on a timer would be a poll standing in for a
/// push that already works.
/// </para>
/// </remarks>
public sealed class AccountSnapshotWorker(
    ClusterStateStore clusterState,
    MembersStore members,
    IClusterTokenService clusterTokens,
    IHttpClientFactory httpClientFactory,
    ClusterOptions cluster,
    UserDirectory users,
    ILogger<AccountSnapshotWorker> logger) : BackgroundService
{
    /// <summary>How often to retry while there is nothing to take a snapshot from.</summary>
    /// <remarks>
    /// Slow on purpose: everything it is waiting for — a holder being assigned, a member becoming
    /// reachable — is measured in a person's time rather than a request's, and until it succeeds this
    /// node still answers from whatever it already holds.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>The holder this node has already taken a full copy from.</summary>
    private string? _takenFrom;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cluster.Enabled)
            return;

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await TryTakeAsync(stoppingToken).ConfigureAwait(false);
            }
            while (_takenFrom is null && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure.
        }
    }

    private async Task TryTakeAsync(CancellationToken ct)
    {
        try
        {
            string? holder = await clusterState.HolderAsync(ClusterCapability.Auth, ct).ConfigureAwait(false);

            // Nobody holds the accounts yet, or this node has not heard who does. Not an error: it is
            // a cluster that has not finished forming.
            if (holder is null)
                return;

            // This node holds them. There is nothing to copy from anybody, and the accounts it serves
            // are already the cluster's.
            if (string.Equals(holder, cluster.MemberId, StringComparison.Ordinal))
            {
                _takenFrom = holder;
                return;
            }

            if (users.Replica is not { } replica)
            {
                logger.LogWarning(
                    "cannot take the cluster's accounts: this node's account store is unavailable ({Reason})",
                    users.UnavailableReason);
                return;
            }

            MemberRow? row = await members.GetByMemberIdAsync(holder, ct).ConfigureAwait(false);
            if (row is null || string.IsNullOrWhiteSpace(row.Url))
            {
                // Known to hold the accounts, and no address to ask at. Worth saying, because from
                // the outside everything looks fine while this node resolves everybody as a stranger.
                logger.LogWarning(
                    "member '{Holder}' holds the cluster's accounts and this node has no address for it",
                    holder);
                return;
            }

            AccountSnapshot? snapshot = await FetchAsync(row, ct).ConfigureAwait(false);
            if (snapshot is null)
                return;

            IReadOnlyList<AccountChange> refused =
                await replica.ApplySnapshotAsync(snapshot, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

            _takenFrom = holder;

            logger.LogInformation(
                "took the cluster's accounts from '{Holder}': {Applied} of {Total}",
                holder, snapshot.Accounts.Count - refused.Count, snapshot.Accounts.Count);

            foreach (AccountChange change in refused)
            {
                // Never merged on a shared username — that is the documented route to handing one
                // person another's access — so it is named instead, once, for somebody to resolve.
                logger.LogError(
                    "account '{Username}' ({Id}) was not taken: this node already has a different "
                    + "account with that name", change.Account.Username, change.Account.UserId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "could not take the cluster's accounts");
        }
    }

    private async Task<AccountSnapshot?> FetchAsync(MemberRow holder, CancellationToken ct)
    {
        MintedClusterToken token = clusterTokens.Mint();
        string url = $"{holder.Url.TrimEnd('/')}/auth/cluster/snapshot";

        HttpClient http = httpClientFactory.CreateClient(OutboxDrainer.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "asking '{Holder}' for the cluster's accounts returned HTTP {Status}",
                holder.MemberId, (int)response.StatusCode);
            return null;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize(
            body, AccountReplicationJson.Default.AccountSnapshot);
    }
}
