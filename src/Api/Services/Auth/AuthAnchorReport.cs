using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// Says, in this service's own log, which member signs people in to this node — and says so loudly when
/// none does.
/// </summary>
/// <remarks>
/// <para>
/// This node accepts only sessions its cluster's auth anchor minted. A node with no cluster secret, or
/// one whose cluster has no member holding the accounts, therefore refuses every sign-in while serving
/// everything else, and from a browser that reads exactly like a wrong password. The log is where an
/// operator looks, so the reason is written there: once when the answer is first known, and again
/// whenever it changes.
/// </para>
/// <para>
/// Read at the gossip cadence, because that is what the answer can change from.
/// </para>
/// </remarks>
public sealed class AuthAnchorReport(
    AnchorHeldGate gate,
    ClusterOptions cluster,
    ILogger<AuthAnchorReport> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cluster.Enabled)
        {
            logger.LogError(
                "This node has no cluster secret, so it has no auth anchor and nobody can sign in to it. "
                + "Every install is a cluster: set Cluster__Secret in /etc/kgsm/kgsm-cluster.env — "
                + "kgsm-base generates one on a machine that founds its own cluster — and run the auth "
                + "anchor, kgsm-auth-anchor, on the machine that holds the accounts.");
            return;
        }

        bool said = false;
        string? reported = null;
        int pass = 0;
        TimeSpan interval = TimeSpan.FromMilliseconds(cluster.GossipMs > 0 ? cluster.GossipMs : 5000);
        using var timer = new PeriodicTimer(interval);

        do
        {
            pass++;
            string? holder;
            try
            {
                holder = (await gate.HolderAsync(stoppingToken).ConfigureAwait(false))?.MemberId;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "could not read which member holds this cluster's accounts");
                continue;
            }

            if (said && string.Equals(holder, reported, StringComparison.Ordinal))
                continue;

            // The first pass after start is allowed to find nobody: gossip may not have arrived yet, and
            // a warning about an absence that resolves a moment later is noise. Every later pass that
            // finds nobody is a real absence.
            if (holder is null && pass == 1)
                continue;

            if (holder is not null)
                logger.LogInformation("sessions on this node are signed by the auth anchor {Holder}", holder);
            else
                logger.LogWarning(
                    "no member of this cluster holds its accounts, so nobody can sign in to this node until "
                    + "an auth anchor claims them");

            said = true;
            reported = holder;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
