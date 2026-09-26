using System.Security.Claims;

using TheKrystalShip.Api.Realtime;
using TheKrystalShip.Api.Services.Aggregation;
using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>
/// What a person may do on this node: every action this node performs, at the cluster, at this node and
/// at each of its instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>A node answers for itself and its instances, and for the actions its own components perform</b> —
/// the engine's and every leaf's, read from the manifests they installed. The anchor answers for
/// <c>auth:*</c>, the assistant for its own; the panel asks each member about what it holds.
/// </para>
/// <para>
/// An instance is named by its install nonce, so one with no nonce read yet is not a target: its grants
/// cannot be told from those of a reinstall under the same name.
/// </para>
/// </remarks>
public sealed class NodeAccess(
    MemberAccess access,
    InstanceCache instances,
    ApiOptions options,
    AuthorityReporterOptions manifests,
    ILogger<NodeAccess> logger)
{
    /// <summary>The report for the caller behind <paramref name="user"/>, or why there is none.</summary>
    public Task<(MemberAccessCaller Caller, AccessReport? Report)> ReportAsync(ClaimsPrincipal user, CancellationToken ct) =>
        access.ReportAsync(user, Targets(), Include(), ct);

    /// <summary>The report for an account already known, for its open connections.</summary>
    public Task<AccessReport?> ReportForAccountAsync(string accountId, CancellationToken ct) =>
        access.ReportForAccountAsync(accountId, Targets(), Include(), ct);

    /// <summary>The cluster, this node, and every instance on it with a nonce.</summary>
    public IReadOnlyList<AccessScope> Targets()
    {
        List<AccessScope> targets = [AccessScope.Cluster];
        if (AccessScope.TryParse("node:" + options.NodeId, out AccessScope? node))
            targets.Add(node.Value);

        foreach (var instance in instances.Roster.Values)
        {
            if (instance.InstallNonce is { Length: > 0 } nonce
                && AccessScope.TryParse($"instance:{options.NodeId}/{instance.Name}#{nonce}", out AccessScope? scope))
            {
                targets.Add(scope.Value);
            }
        }

        return targets;
    }

    /// <summary>
    /// Whether an action is one this node performs: its namespace is the component of a manifest this
    /// node's components installed.
    /// </summary>
    public Func<string, bool> Include()
    {
        HashSet<string> components = new(Manifests().Select(m => m.Component), StringComparer.Ordinal);

        return action =>
        {
            int colon = action.IndexOf(':', StringComparison.Ordinal);
            return colon > 0 && components.Contains(action[..colon]);
        };
    }

    /// <summary>
    /// The report for the synthetic caller of an auth-disabled host: an Owner, holding every action
    /// this node's components declare, cluster-wide.
    /// </summary>
    /// <remarks>
    /// Such a host authenticates every request without asking anybody who they are, so there is no
    /// account to evaluate and the answer is the one its every policy already gives.
    /// </remarks>
    public AccessReport SyntheticOwner()
    {
        string[] actions = [.. Manifests().SelectMany(m => m.CatalogActions()).Select(a => a.Id)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        return new AccessReport(0, Current: true, actions,
            new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, IReadOnlyList<string>>(),
            Owner: true);
    }

    private IEnumerable<ActionManifest> Manifests()
    {
        foreach (string directory in manifests.ManifestDirectories)
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (ActionManifests.TryRead(file, out string? problem) is { } manifest)
                    yield return manifest;
                else
                    logger.LogDebug("action manifest {File} left out: {Problem}", file, problem);
            }
        }
    }
}

/// <summary>
/// Carries a fresh <c>/me/access</c> to every open connection subscribed to <c>me</c> whenever this
/// node's replica takes a change, so an open panel re-gates its controls without a reload.
/// </summary>
public sealed class MeAccessPush(StreamHub hub, NodeAccess access) : IAuthorityChangeListener
{
    /// <inheritdoc />
    public async Task AuthorityChangedAsync(CancellationToken ct)
    {
        foreach (string accountId in hub.AccountsSubscribedTo(StreamProtocol.MeTopic))
        {
            if (await access.ReportForAccountAsync(accountId, ct).ConfigureAwait(false) is not { } report)
                continue;

            hub.PublishToAccount(accountId, StreamProtocol.MeTopic, StreamProtocol.MeAccessEntityKey,
                new StreamMessage(StreamProtocol.MeTopic, StreamProtocol.MeAccess, report));
        }
    }
}
