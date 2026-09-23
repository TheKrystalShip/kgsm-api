using System.Text.Json;

using TheKrystalShip.Api.Realtime;
using TheKrystalShip.KGSM.Auth;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// An account change replicated from the auth anchor, applied to this node's replica and then carried
/// to every open stream held by that account.
/// </summary>
/// <remarks>
/// <para>
/// The anchor is the only writer of accounts, so replication is how an admin's approval, demotion or
/// switch-off arrives here at all. Applying it is the shared handler's job; what this adds is the
/// node's half: the cached authority answers for the account are dropped, and a connection held by
/// the person is re-gated and told at once, rather than at its own periodic re-read. Somebody approved
/// at the anchor is very often somebody sitting in front of a panel waiting for it.
/// </para>
/// <para>
/// <b>What is pushed is what the replica holds after applying, never what the message said.</b> The bus
/// delivers at least once and a redelivery can be older than what is held — the replica refuses it,
/// and pushing its values anyway would tell a panel something the node does not believe.
/// </para>
/// </remarks>
public sealed class AccountChangesReachOpenStreams(
    AccountReplicationHandler inner,
    UserDirectory users,
    StreamHub hub,
    ILogger<AccountChangesReachOpenStreams> logger) : IClusterMessageHandler
{
    public string Type => inner.Type;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        await inner.HandleAsync(envelope, ct).ConfigureAwait(false);

        string? userId;
        try
        {
            userId = envelope.Payload.Deserialize(AccountReplicationJson.Default.AccountChange)?.Account?.UserId;
        }
        catch (JsonException)
        {
            // The handler above has already said why it dropped this, and there is nobody to tell.
            return;
        }

        if (string.IsNullOrEmpty(userId))
            return;

        try
        {
            if (await users.ReloadAsync(userId, ct).ConfigureAwait(false) is { } account)
                hub.AuthorityChanged(account.UserId, account.EffectiveTier, UserStatuses.ToWire(account.Status));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The change is applied; only the courtesy failed. An open stream still picks it up at its
            // own re-read, so this is worth a line and never a retry of the whole message.
            logger.LogWarning(e, "account {Account} replicated, but open streams could not be told", userId);
        }
    }
}

/// <summary>
/// An account removed at the auth anchor, taken out of this node's replica and cut from every open
/// stream held by it.
/// </summary>
/// <remarks>
/// A removed account holds nothing, so its connections are re-gated to <see cref="KgsmTier.None"/> at
/// once and told the account is no longer known here — the same answer <c>GET /me</c> gives for it.
/// </remarks>
public sealed class AccountRemovalsReachOpenStreams(
    AccountRemovalHandler inner,
    UserDirectory users,
    StreamHub hub) : IClusterMessageHandler
{
    public string Type => inner.Type;

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        await inner.HandleAsync(envelope, ct).ConfigureAwait(false);

        string? userId;
        try
        {
            userId = envelope.Payload.Deserialize(AccountReplicationJson.Default.AccountRemoval)?.UserId;
        }
        catch (JsonException)
        {
            return;
        }

        if (string.IsNullOrEmpty(userId))
            return;

        // Still present means the removal was older than what is held and the replica refused it.
        if (await users.ReloadAsync(userId, ct).ConfigureAwait(false) is null)
            hub.AuthorityChanged(userId, KgsmTier.None, AccountStanding.UnknownStatus);
    }
}
