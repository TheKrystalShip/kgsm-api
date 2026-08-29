using TheKrystalShip.KGSM.Cluster.Identity;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// The roster-backed member gate (<c>PLAN-peers.md §0</c> #8, §2 #7/#8): a member bearing a validly-signed
/// service token is enabled unless its row is explicitly disabled. Absence from the roster is not rejection — only an explicit disable is, because
/// the shared secret alone already proves membership, so an unknown-but-validly-tokened member is trusted
/// rather than refused. That is what keeps the mesh working while members still hold a partial view of each
/// other.
/// </summary>
/// <remarks>
/// Keyed on the member id the token's <c>iss</c> carries, never on a host: two members on one machine are
/// two members, and disabling one has to leave the other running.
/// </remarks>
public sealed class PeersTableGate(MembersStore members, ILogger<PeersTableGate> logger) : IClusterMemberGate
{
    /// <inheritdoc/>
    public async Task<bool> IsEnabledAsync(string memberId)
    {
        MemberRow? row = await members.GetByMemberIdAsync(memberId, CancellationToken.None).ConfigureAwait(false);
        if (row is not null && !row.Enabled)
        {
            logger.LogDebug("Rejecting cluster call from disabled member {MemberId}.", memberId);
            return false;
        }
        return true;
    }
}
