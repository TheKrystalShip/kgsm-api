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

    /// <summary>This node, as a target.</summary>
    public AccessScope NodeTarget => AccessScope.ForNode(options.NodeId);

    /// <summary>
    /// A server on this node, as a target: its install, once the engine has reported the nonce that
    /// names it, and this node until then — so a grant on the one install cannot reach a server whose
    /// install cannot yet be told from a reinstall.
    /// </summary>
    public AccessScope ServerTarget(string serverId) =>
        instances.Roster.TryGetValue(serverId, out var instance) && instance.InstallNonce is { Length: > 0 } nonce
            ? AccessScope.ForInstance(options.NodeId, serverId, nonce)
            : NodeTarget;

    /// <summary>The route parameter a server route names its server by.</summary>
    public const string ServerParameter = "id";

    /// <summary>
    /// Whether a route template is a server's — evaluated at that server's install. The operations this
    /// node publishes say the same, from this one test.
    /// </summary>
    public static bool IsServerRoute(string pattern) =>
        pattern.TrimStart('/').StartsWith("api/v1/servers/{" + ServerParameter + "}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The target a request names: a server route (<c>api/v1/servers/{id}/…</c>) is that server, and
    /// everything else is this node — the cluster-wide actions widen from it on their own.
    /// </summary>
    public AccessScope TargetOf(HttpContext? http)
    {
        if (http?.GetEndpoint() is RouteEndpoint route
            && route.RoutePattern.RawText is { } pattern
            && IsServerRoute(pattern)
            && http.Request.RouteValues[ServerParameter] is string serverId)
        {
            return ServerTarget(serverId);
        }

        return NodeTarget;
    }

    /// <summary>
    /// Whether the caller behind <paramref name="user"/> may perform <paramref name="action"/> at
    /// <paramref name="target"/>, from this node's replica as it stands.
    /// </summary>
    /// <remarks>
    /// A host run with auth switched off authenticates everybody as its synthetic Owner, so every action
    /// is allowed there. A caller with no account here is refused everything, and so is every caller
    /// while the replica cannot be read — authentication has already answered those with an outage.
    /// </remarks>
    public async Task<bool> AllowsAsync(ClaimsPrincipal user, string action, AccessScope target, CancellationToken ct = default)
    {
        if (options.AuthDisabled)
            return user.Identity?.IsAuthenticated == true;

        return await AllowsAccountAsync(AccountOf(user), action, target, ct).ConfigureAwait(false);
    }

    /// <summary>The account authentication resolved <paramref name="user"/> to here, or <see langword="null"/>.</summary>
    public static string? AccountOf(ClaimsPrincipal user) =>
        user.FindFirst(AccessClaims.Account)?.Value is { Length: > 0 } account ? account : null;

    /// <summary>
    /// Whether <paramref name="accountId"/> may perform <paramref name="action"/> at
    /// <paramref name="target"/> — for a credential handed out ahead of use (a download ticket, a push
    /// button), evaluated again when it is presented. An auth-disabled host allows it; no account, or an
    /// unreadable replica, refuses it.
    /// </summary>
    public async Task<bool> AllowsAccountAsync(string? accountId, string action, AccessScope target, CancellationToken ct = default)
    {
        if (options.AuthDisabled)
            return true;

        if (accountId is null)
            return false;

        return await access.EvaluatorAsync(ct).ConfigureAwait(false) is { } evaluator
            && evaluator.Allows(accountId, action, target).Allowed;
    }

    /// <summary>Whether the caller may perform <paramref name="action"/> on one server of this node.</summary>
    public Task<bool> AllowsOnServerAsync(ClaimsPrincipal user, string action, string serverId, CancellationToken ct = default) =>
        AllowsAsync(user, action, ServerTarget(serverId), ct);

    /// <summary>
    /// The servers among <paramref name="serverIds"/> the caller may perform <paramref name="action"/> on
    /// — how a collection is cut to what its reader can see (permissions plan §4·c).
    /// </summary>
    public async Task<HashSet<string>> AllowedServersAsync(
        ClaimsPrincipal user, string action, IEnumerable<string> serverIds, CancellationToken ct = default)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal);
        if (options.AuthDisabled)
        {
            allowed.UnionWith(serverIds);
            return allowed;
        }

        if (AccountOf(user) is not { } account
            || await access.EvaluatorAsync(ct).ConfigureAwait(false) is not { } evaluator)
        {
            return allowed;
        }

        foreach (string id in serverIds)
        {
            if (evaluator.Allows(account, action, ServerTarget(id)).Allowed)
                allowed.Add(id);
        }

        return allowed;
    }

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
