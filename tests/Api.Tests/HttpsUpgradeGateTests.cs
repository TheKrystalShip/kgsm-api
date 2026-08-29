using System.Net;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// Who gets sent to https. "No bare HTTP on the internet" is a statement about the CALLER, so the gate reads
/// the caller's address and nothing else. A caller inside the operator's own network — the deploy's health
/// probe on loopback, a reverse proxy on another machine that has already terminated TLS for the real
/// client — crosses nothing that needs protecting, and redirecting one sends it to an address that lands
/// straight back here: a loop rather than an upgrade.
/// </summary>
public sealed class HttpsUpgradeGateTests
{
    [Theory]
    [InlineData("127.0.0.1")]          // the deploy's own /health probe
    [InlineData("::1")]
    [InlineData("192.168.1.128")]      // a reverse proxy on another machine in this network
    [InlineData("10.44.0.4")]
    [InlineData("172.16.5.9")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.1.1")]        // link-local
    [InlineData("fd00::1")]            // unique-local
    [InlineData("fe80::1")]            // IPv6 link-local
    public void ACallerInsideTheOperatorsNetworkIsLeftAlone(string address) =>
        Assert.False(Startup.IsOutOnTheInternet(IPAddress.Parse(address)));

    [Theory]
    [InlineData("95.19.50.122")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]         // just below the private block
    [InlineData("172.32.0.1")]         // just above it
    [InlineData("192.169.0.1")]        // one off 192.168
    [InlineData("169.253.0.1")]        // one off link-local
    [InlineData("2606:4700::1111")]
    public void ACallerOutOnTheInternetIsUpgraded(string address) =>
        Assert.True(Startup.IsOutOnTheInternet(IPAddress.Parse(address)));

    [Fact]
    public void AnIPv4CallerArrivingMappedIntoIPv6IsReadAsTheIPv4ItIs()
    {
        // A dual-stack socket presents an IPv4 peer as ::ffff:a.b.c.d. Read as raw IPv6 that is neither
        // loopback nor unique-local, so a proxy on the LAN would be treated as a stranger and redirected
        // into the loop this gate exists to avoid.
        Assert.False(Startup.IsOutOnTheInternet(IPAddress.Parse("::ffff:192.168.1.128")));
        Assert.True(Startup.IsOutOnTheInternet(IPAddress.Parse("::ffff:95.19.50.122")));
    }
}
