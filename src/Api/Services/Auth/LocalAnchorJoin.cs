using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Introduces this node to the auth anchor on its own machine while the node knows no member at all.
/// </summary>
/// <remarks>
/// <para>
/// A machine that founded its own cluster runs the anchor beside this node, and two members only find
/// each other through a handshake. Adding a member is an admin action, and nobody can sign in to take it
/// until this node knows who holds the accounts — so on a fresh machine the node takes that one step
/// itself, against <see cref="ApiOptions.LocalAnchorUrl"/>, exactly as an admin pasting that address
/// would.
/// </para>
/// <para>
/// <b>Only on the machine that founded the cluster, and only while the roster is empty.</b> A machine
/// that did not found the cluster it is in — one given another cluster's secret, including a founding
/// machine that has since taken one — is joined by an admin adding it; the anchor beside it holds
/// nothing there, and introducing itself would put that anchor in the roster as a member nobody asked
/// for. A node that knows any member has been joined to a cluster by someone, and whether that
/// cluster's anchor runs here is not this node's to decide.
/// </para>
/// </remarks>
public sealed class LocalAnchorJoin(
    ApiOptions options,
    ClusterOptions cluster,
    MembersStore members,
    MemberHandshakeService handshake,
    ILogger<LocalAnchorJoin> logger) : BackgroundService
{
    /// <summary>How long between introductions that went unanswered. The anchor is a local process, so
    /// a machine that runs one answers within its own start-up; a longer wait costs nothing.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cluster.Enabled || string.IsNullOrEmpty(options.LocalAnchorUrl))
            return;

        if (!ClusterFounding.IsFoundedHere(cluster))
        {
            logger.LogInformation(
                "this machine did not found the cluster it is in, so this node waits for an admin to add "
                + "it rather than introducing itself to the auth anchor at {Url}", options.LocalAnchorUrl);
            return;
        }

        bool saidUnanswered = false;
        using var timer = new PeriodicTimer(RetryInterval);

        do
        {
            try
            {
                if ((await members.ListAsync(stoppingToken).ConfigureAwait(false)).Count > 0)
                    return;

                MemberAddResult result = await handshake
                    .AddMemberAsync(options.LocalAnchorUrl, nickname: null, stoppingToken)
                    .ConfigureAwait(false);

                if (result.Outcome == MemberAddOutcome.Added)
                {
                    logger.LogInformation(
                        "joined the auth anchor on this machine at {Url} as {Member}",
                        options.LocalAnchorUrl, result.Member?.MemberId);
                    return;
                }

                // Said once: a node whose machine did not found its cluster sees this on every start
                // until an admin adds it somewhere, and it is not a fault there.
                if (!saidUnanswered)
                {
                    logger.LogInformation(
                        "this node knows no member yet, and the auth anchor at {Url} did not join ({Outcome}); "
                        + "asking again every {Seconds}s until it does or an admin adds this node to a cluster",
                        options.LocalAnchorUrl, result.Outcome, (int)RetryInterval.TotalSeconds);
                    saidUnanswered = true;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "could not introduce this node to the auth anchor at {Url}", options.LocalAnchorUrl);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
