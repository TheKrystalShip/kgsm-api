using System.Text.Json;

using TheKrystalShip.Api.Services.Auth;

using TheKrystalShip.KGSM.Auth.Users;
using TheKrystalShip.KGSM.Cluster.Messaging;

namespace TheKrystalShip.Api.Services.Cluster.Handlers;

/// <summary>
/// Applies an account change published by the member holding the cluster's accounts.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a node able to answer <em>who is this and what may they do</em> without leaving
/// the machine — and therefore what lets serving, streaming and session refresh carry on while the
/// member holding the accounts is unreachable. The node gains no process for it: it already has an
/// inbox, and this is a handler on it.
/// </para>
/// <para>
/// The work is <see cref="AccountReplica"/>'s, in the shared auth package, so a replica applies a
/// change identically wherever it runs rather than each member growing its own reading of the rules.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A <c>500</c> is the only answer that keeps a message in the sender's
/// outbox, and every outcome this handler can reach is permanent: a stale change will never become
/// newer, and a username conflict will never resolve itself by being retried. Throwing would wedge
/// the sender's queue behind a message it can never deliver, taking every later account change with
/// it — including a disable.
/// </para>
/// </remarks>
public sealed class AccountReplicationHandler(
    UserDirectory users,
    ILogger<AccountReplicationHandler> logger) : IClusterMessageHandler
{
    public string Type => "account.changed";

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        if (users.Replica is not { } replica)
        {
            // Acknowledged rather than retried: the store will not become readable by being asked
            // again, and a node that cannot read accounts already says so as a capability.
            logger.LogError(
                "cluster account.changed (id={Id} from={From}) dropped — this node's account store is "
                + "unavailable: {Reason}", envelope.Id, envelope.From, users.UnavailableReason);
            return;
        }

        AccountChange? change;
        try
        {
            change = envelope.Payload.Deserialize(AccountReplicationJson.Default.AccountChange);
        }
        catch (JsonException ex)
        {
            // A payload this build cannot read will not become readable on a retry.
            logger.LogWarning(ex,
                "cluster account.changed (id={Id} from={From}): unreadable payload — dropped",
                envelope.Id, envelope.From);
            return;
        }

        if (change?.Account is null)
        {
            logger.LogWarning(
                "cluster account.changed (id={Id} from={From}): no account in payload — dropped",
                envelope.Id, envelope.From);
            return;
        }

        ReplicationOutcome outcome =
            await replica.ApplyAsync(change, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        switch (outcome)
        {
            case ReplicationOutcome.Applied:
                logger.LogInformation(
                    "account {Account} replicated at version {Version} from {From}",
                    change.Account.UserId, change.Version, envelope.From);
                break;

            case ReplicationOutcome.Stale:
                // The ordinary case under at-least-once delivery, and the case the version rule exists
                // for. Debug, because a redelivery is not news.
                logger.LogDebug(
                    "account {Account} at version {Version} is not newer than what this member holds — dropped",
                    change.Account.UserId, change.Version);
                break;

            case ReplicationOutcome.UsernameConflict:
                // Loud, because nothing will fix it on its own and the account is not being replicated
                // here until somebody does: this member had its own account by that name before it
                // joined, and merging two accounts because they share a name is how one person is
                // handed another's access.
                logger.LogError(
                    "account {Account} could not be replicated: this member already has a different "
                    + "account named '{Username}'. It will not be replicated until one is renamed.",
                    change.Account.UserId, change.Account.Username);
                break;
        }
    }
}

/// <summary>Applies the removal of an account published by the member holding the cluster's accounts.</summary>
/// <remarks>
/// A separate type from a change rather than a flag on one, because it means something different to
/// every member: a change says what an account is now, and this says there is no longer an account to
/// say anything about. The version row it leaves behind is what stops a change issued before it
/// arriving later and re-creating somebody who was deliberately removed.
/// </remarks>
public sealed class AccountRemovalHandler(
    UserDirectory users,
    ILogger<AccountRemovalHandler> logger) : IClusterMessageHandler
{
    public string Type => "account.removed";

    public async Task HandleAsync(ClusterEnvelope envelope, CancellationToken ct)
    {
        if (users.Replica is not { } replica)
        {
            logger.LogError(
                "cluster account.removed (id={Id} from={From}) dropped — this node's account store is "
                + "unavailable: {Reason}", envelope.Id, envelope.From, users.UnavailableReason);
            return;
        }

        AccountRemoval? removal;
        try
        {
            removal = envelope.Payload.Deserialize(AccountReplicationJson.Default.AccountRemoval);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "cluster account.removed (id={Id} from={From}): unreadable payload — dropped",
                envelope.Id, envelope.From);
            return;
        }

        if (removal is null || string.IsNullOrWhiteSpace(removal.UserId))
        {
            logger.LogWarning(
                "cluster account.removed (id={Id} from={From}): no account in payload — dropped",
                envelope.Id, envelope.From);
            return;
        }

        ReplicationOutcome outcome =
            await replica.RemoveAsync(removal, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        if (outcome == ReplicationOutcome.Applied)
        {
            logger.LogInformation(
                "account {Account} removed at version {Version} from {From}",
                removal.UserId, removal.Version, envelope.From);
        }
        else
        {
            logger.LogDebug(
                "account {Account} removal at version {Version} is not newer than what this member holds",
                removal.UserId, removal.Version);
        }
    }
}
