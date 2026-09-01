using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Api;
using TheKrystalShip.Api.Services.Cluster;
using TheKrystalShip.KGSM.Cluster;

namespace Api.Tests;

/// <summary>
/// A member of a cluster carries identity assertions between machines, so it says so when its own
/// addressing would put one on a network in clear.
/// </summary>
/// <remarks>
/// The default matters as much as the misconfiguration: <see cref="ApiOptions"/> falls back to
/// <c>http://0.0.0.0:8080</c>, so a member that was never told where to listen lands on the wrong side of
/// this rule by doing nothing at all. That case is pinned here rather than left to be discovered on a node.
/// </remarks>
public class MemberWireAdvisoryTests
{
    [Fact]
    public void A_loopback_bind_with_an_https_address_says_nothing() =>
        Assert.Empty(Warnings(urls: "http://127.0.0.1:8080", publicBaseUrl: "https://hotbox.example.com"));

    [Fact]
    public void A_wildcard_bind_in_clear_is_reported()
    {
        string warning = Assert.Single(Warnings(urls: "http://0.0.0.0:8080"));
        Assert.Contains("http://0.0.0.0:8080", warning);
        Assert.Contains("Api__Urls", warning);
    }

    [Fact]
    public void The_default_addressing_is_reported()
    {
        // Nothing configured but the host id: the fallback binds every address in clear.
        string warning = Assert.Single(Warnings(urls: null));
        Assert.Contains("0.0.0.0:8080", warning);
    }

    [Fact]
    public void A_plain_http_address_offered_to_other_members_is_reported()
    {
        string warning = Assert.Single(
            Warnings(urls: "http://127.0.0.1:8080", publicBaseUrl: "http://192.168.1.129:8080"));
        Assert.Contains("Api__PublicBaseUrl", warning);
    }

    [Fact]
    public void A_plain_http_gossip_address_is_reported()
    {
        string warning = Assert.Single(
            Warnings(urls: "http://127.0.0.1:8080", gossipUrl: "http://10.0.0.4:8080"));
        Assert.Contains("Api__ClusterGossipUrl", warning);
    }

    [Fact]
    public void The_certificate_challenge_port_is_not_an_api_surface() =>
        // :80 redirects and answers the ACME challenge; it carries nothing between members.
        Assert.Empty(Warnings(urls: "http://0.0.0.0:80;https://0.0.0.0:443;http://127.0.0.1:8097"));

    [Fact]
    public void A_host_with_no_cluster_secret_is_not_a_member() =>
        Assert.Empty(Warnings(urls: "http://0.0.0.0:8080", secret: ""));

    private static IReadOnlyList<string> Warnings(
        string? urls, string? publicBaseUrl = null, string? gossipUrl = null, string secret = "shared")
    {
        var settings = new Dictionary<string, string?> { ["Api:HostId"] = "hotbox" };
        if (urls is not null) settings["Api:Urls"] = urls;
        if (publicBaseUrl is not null) settings["Api:PublicBaseUrl"] = publicBaseUrl;
        if (gossipUrl is not null) settings["Api:ClusterGossipUrl"] = gossipUrl;

        ApiOptions options = ApiOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        var logger = new CapturingLogger();
        new MemberWireAdvisory(options, new ClusterOptions { MemberId = "hotbox", Secret = secret, StorePath = ":memory:" }, logger)
            .StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return logger.Warnings;
    }

    private sealed class CapturingLogger : ILogger<MemberWireAdvisory>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
