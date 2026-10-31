using System.Security.Claims;

using TheKrystalShip.Api.Realtime;

namespace TheKrystalShip.Api.Services.Auth;

/// <summary>The directories this node's action manifests are installed in.</summary>
public sealed record ManifestSources(IReadOnlyList<string> Directories);

/// <summary>
/// What a caller may do on this node: the actions their token lists.
/// </summary>
/// <remarks>
/// <para>
/// A grant is cluster-wide, so there is no target to evaluate it at: a caller holds an action or does
/// not. A person holds what their access token lists, as of the moment it was issued, and a role change
/// reaches them at their next renewal. Another member acting as itself, and the open caller of a host
/// with authentication disabled, hold every action.
/// </para>
/// <para>
/// <b>A node answers for the actions its own components perform</b> — the engine's and every leaf's, read
/// from the manifests they installed. The token lists the actions of the whole cluster's application, and
/// the report here keeps the ones this node performs.
/// </para>
/// </remarks>
public sealed class NodeAccess(ApiOptions options, ManifestSources manifests, ILogger<NodeAccess> logger)
{
    /// <summary>
    /// The actions the caller behind <paramref name="user"/> holds, or <see langword="null"/> when they
    /// hold every action.
    /// </summary>
    private IReadOnlySet<string>? HeldBy(ClaimsPrincipal user) =>
        CallerId.Of(user)?.Kind switch
        {
            CallerKind.Member or CallerKind.Open => null,
            CallerKind.Person when user.Identity is ClaimsIdentity identity =>
                identity.FindAll(options.SessionsActionsClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal),
            _ => new HashSet<string>(),
        };

    /// <summary>The actions the caller behind <paramref name="user"/> holds, in order; empty for a caller who holds every action.</summary>
    public IReadOnlyList<string> HeldList(ClaimsPrincipal user) =>
        HeldBy(user) is { } held ? [.. held.Order(StringComparer.Ordinal)] : [];

    /// <summary>What the stream reader behind <paramref name="user"/> may see.</summary>
    public StreamAccess StreamAccessOf(ClaimsPrincipal user) => HeldBy(user) is not { } held
        ? StreamAccess.Unrestricted
        : CallerId.Of(user) is { } caller ? StreamAccess.For(held) : StreamAccess.Nobody;

    /// <summary>Whether the caller behind <paramref name="user"/> holds <paramref name="action"/>.</summary>
    public bool Allows(ClaimsPrincipal user, string action) => HeldBy(user) is not { } held || held.Contains(action);

    /// <summary>
    /// The servers among <paramref name="serverIds"/> the caller may perform <paramref name="action"/> on
    /// — how a collection is cut to what its reader can see. A grant is cluster-wide, so it is all of
    /// them or none.
    /// </summary>
    public HashSet<string> AllowedServers(ClaimsPrincipal user, string action, IEnumerable<string> serverIds) =>
        Allows(user, action) ? new HashSet<string>(serverIds, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The report for the caller behind <paramref name="user"/>: the actions they hold among those this
    /// node performs, or all of them for a caller who holds everything.
    /// </summary>
    public AccessReport ReportFor(ClaimsPrincipal user)
    {
        string[] performed = [.. Manifests(manifests.Directories).SelectMany(m => m.ActionIds())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        if (HeldBy(user) is not { } held)
            return AccessReport.Of(performed, owner: CallerId.Of(user)?.Kind == CallerKind.Open);

        return AccessReport.Of([.. performed.Where(held.Contains)]);
    }

    /// <summary>
    /// The document the provider pulls: the manifests of this node's engine, its leaves and the anchors
    /// beside it.
    /// </summary>
    public NodeActionManifest ServedManifest() => NodeActionManifest.From(
        Manifests(manifests.Directories.Append(Path.Combine(options.AnchorDescriptorDir, "actions"))));

    private IEnumerable<ActionManifest> Manifests(IEnumerable<string> directories)
    {
        foreach (string directory in directories)
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
