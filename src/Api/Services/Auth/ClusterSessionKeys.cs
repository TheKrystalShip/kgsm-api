using Microsoft.IdentityModel.Tokens;

using TheKrystalShip.KGSM.Auth.Sessions;
using TheKrystalShip.KGSM.Cluster;
using TheKrystalShip.KGSM.Cluster.Membership;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// What this node currently knows about verifying sessions its cluster's auth anchor minted.
/// </summary>
/// <remarks>
/// <para>
/// Both facts are read <b>through the holder</b> of the <c>auth</c> capability rather than off
/// whichever member happens to state them. That is the whole security of it: a key taken from any
/// member would let any member substitute the one sessions are verified against, so substituting a
/// key has to mean reassigning the capability — which is a visible change to cluster state instead of
/// a silent one.
/// </para>
/// <para>
/// <b>Nothing here can mint.</b> A published key is a public point; the verifier built from it checks
/// a signature and cannot produce one. That is what makes a session valid on every member safe to
/// accept on a member that did not issue it.
/// </para>
/// <para>
/// <b>Knowing nothing is a real answer and it fails closed.</b> A host with no cluster, one that has
/// not heard who holds the accounts, or one whose holder states no audience or issuer, accepts no
/// cluster session at all — it goes on serving its own. Guessing either would accept a token minted
/// for a different cluster, so the absent case refuses rather than assumes.
/// </para>
/// </remarks>
public sealed class ClusterSessionKeys(
    ClusterFacts facts,
    ClusterOptions cluster,
    ILogger<ClusterSessionKeys> logger) : BackgroundService, IClusterSessionKeys
{
    /// <summary>
    /// What is being read is what gossip moves, so re-reading faster than gossip changes it buys
    /// nothing.
    /// </summary>
    private TimeSpan Interval =>
        TimeSpan.FromMilliseconds(cluster.GossipMs > 0 ? cluster.GossipMs : 5000);

    private volatile State _state = State.Unknown;

    /// <inheritdoc />
    public string? Audience => _state.Audience;

    /// <inheritdoc />
    public string? Issuer => _state.Issuer;

    /// <inheritdoc />
    public IReadOnlyList<SecurityKey> Keys => _state.Keys;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cluster.Enabled)
        {
            // Not a misconfiguration. A host that is not in a cluster mints and verifies its own
            // sessions and there is no second kind to accept.
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The host is stopping. Not a failure.
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        string? published;
        string? audience;
        string? issuer;
        try
        {
            published = await facts
                .FromHolderAsync(ClusterCapability.Auth, ClusterAuthFacts.PublicKey, ct)
                .ConfigureAwait(false);
            audience = await facts
                .FromHolderAsync(ClusterCapability.Auth, ClusterAuthFacts.Audience, ct)
                .ConfigureAwait(false);
            issuer = await facts
                .FromHolderAsync(ClusterCapability.Auth, ClusterAuthFacts.Issuer, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One failed read must not change what this node accepts. "I could not find out" is not
            // "the anchor published nothing", and treating it as the second would sign the whole
            // cluster out of this node over a locked database file.
            logger.LogWarning(e, "Could not read the cluster's session verification keys.");
            return;
        }

        State current = _state;
        if (string.Equals(published, current.Published, StringComparison.Ordinal)
            && string.Equals(audience, current.Audience, StringComparison.Ordinal)
            && string.Equals(issuer, current.Issuer, StringComparison.Ordinal))
        {
            return;
        }

        IReadOnlyList<SecurityKey> keys = Read(published);
        _state = new State(audience, issuer, keys, published);

        if (keys.Count == 0 || string.IsNullOrEmpty(audience) || string.IsNullOrEmpty(issuer))
        {
            logger.LogWarning(
                "The member holding this cluster's accounts states {Keys} verification key(s), "
                + "audience '{Audience}' and issuer '{Issuer}' — sessions it minted are refused here "
                + "until it states all three.",
                keys.Count, audience, issuer);
            return;
        }

        logger.LogInformation(
            "Verifying cluster sessions issued by '{Issuer}' for '{Audience}' against {Count} "
            + "published key(s): {Kids}.",
            issuer, audience, keys.Count, string.Join(", ", keys.Select(k => k.KeyId)));
    }

    /// <summary>
    /// The verification keys a published set describes, or none when it describes nothing this build
    /// can verify with.
    /// </summary>
    /// <remarks>
    /// A set that will not parse is treated as no keys rather than as a reason to fail: it is a
    /// statement by another member, and a member that states something unreadable must not be able to
    /// stop this one serving.
    /// </remarks>
    private IReadOnlyList<SecurityKey> Read(string? published)
    {
        if (string.IsNullOrWhiteSpace(published))
            return [];

        try
        {
            return EcdsaSessionSigner.ReadKeys(published) is { } set
                ? [.. EcdsaSessionSigner.VerificationKeysFrom(set)]
                : [];
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "The member holding this cluster's accounts published a key set this node cannot read.");
            return [];
        }
    }

    /// <summary>One consistent answer, replaced whole so a reader never sees a key set beside an
    /// audience or an issuer it was not published with.</summary>
    private sealed record State(
        string? Audience, string? Issuer, IReadOnlyList<SecurityKey> Keys, string? Published)
    {
        public static readonly State Unknown = new(null, null, [], null);
    }
}
