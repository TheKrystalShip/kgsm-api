using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Cluster;

namespace TheKrystalShip.Api.Services.Cluster;

/// <summary>
/// Reports, once at startup, whether this member's own addressing carries what it says between machines
/// in clear.
/// </summary>
/// <remarks>
/// <para>
/// A member-to-member call names the account it acts for, and the node at the far end resolves that
/// account's authority from its own replica. That header is an identity assertion, so it does not belong
/// on a network in clear — which makes a plain-http listener on anything but loopback, and a plain-http
/// address offered to other members, both worth saying out loud.
/// </para>
/// <para>
/// The pattern this asks for is TLS terminated in front of a loopback bind: a reverse proxy holds the
/// certificate, the service binds <c>127.0.0.1</c>, and one certificate lifecycle serves every member on
/// the machine rather than one inside each component.
/// </para>
/// <para>
/// <b>It reports and does not refuse.</b> The deploy refuses, before anything is stopped and while
/// somebody is watching. Here the same finding arrives on a node that is already serving a cluster, and a
/// process that exited over it would take a working member down on an upgrade — trading a readable header
/// for an unreachable machine.
/// </para>
/// <para>
/// <b>This is the only place the rule reaches a node installed from a package</b>, which never runs the
/// deploy script. That is what it exists for, so it names the setting and the fix rather than only the
/// fault.
/// </para>
/// <para>
/// A host with no cluster secret is not a member and is not checked: it answers whoever is on its own
/// network, which is what a standalone install is.
/// </para>
/// </remarks>
public sealed class MemberWireAdvisory(
    ApiOptions options,
    ClusterOptions cluster,
    ILogger<MemberWireAdvisory> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!cluster.Enabled) return Task.CompletedTask;

        foreach (string url in options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Reachable(url)) continue;
            // :80 answers the certificate challenge and redirects everything else, so it serves no API
            // surface and carries nothing between members.
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? bound) && bound.Port == 80) continue;
            logger.LogWarning(
                "This member listens on {Url}, which serves its API in clear on an address other machines "
                + "can reach. A member-to-member call names the account it acts for. Bind Api__Urls to "
                + "127.0.0.1 and terminate TLS in front of it.", url);
        }

        if (Reachable(options.PublicBaseUrl))
            logger.LogWarning(
                "This member offers other members the plain-http address {Url}. State the https address "
                + "the panel and other members reach it at in Api__PublicBaseUrl.", options.PublicBaseUrl);

        if (Reachable(options.ClusterGossipUrl))
            logger.LogWarning(
                "This member offers other members the plain-http address {Url} to gossip on. Set "
                + "Api__ClusterGossipUrl to an https address, or leave it unset and let one address serve "
                + "browsers and members alike.", options.ClusterGossipUrl);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Whether an address carries plain http to somewhere other than the machine reading it. A
    /// loopback bind is unreachable from another machine, so nothing it carries crosses a network; a
    /// wildcard bind answers on every address this machine has, so it is reachable by definition.</summary>
    private static bool Reachable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.IsLoopback) return false;
        return true;
    }
}
