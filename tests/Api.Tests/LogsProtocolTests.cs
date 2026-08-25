using TheKrystalShip.Api.Realtime;

using TheKrystalShip.KGSM.Auth;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The host-logs realtime vocabulary + the operator-gate predicate. The same <see cref="StreamProtocol.RequiresOperator"/>
/// the WS subscribe path uses to refuse a viewer's <c>hosts/{id}/logs</c> subscription (raw journald can leak
/// secrets) is asserted here as a pure unit, alongside the REST tier-gate in <c>TierMatrixTests</c>.
/// </summary>
public sealed class LogsProtocolTests
{
    [Fact]
    public void HostLogsTopic_IsHostScoped() =>
        Assert.Equal("hosts/hotrod/logs", StreamProtocol.HostLogsTopic("hotrod"));

    [Theory]
    [InlineData("hosts/hotrod/logs", true)]
    [InlineData("hosts/test-host/logs", true)]
    [InlineData("hosts/hotrod/metrics", false)]
    [InlineData("hosts/hotrod/capabilities", false)]
    [InlineData("servers/factorio/console", false)]
    [InlineData("audit", false)]
    public void IsHostLogsTopic_MatchesOnlyHostLogs(string topic, bool expected) =>
        Assert.Equal(expected, StreamProtocol.IsHostLogsTopic(topic));

    [Theory]
    [InlineData("hosts/hotrod/logs", true)]   // the one operator-gated topic today
    [InlineData("hosts/hotrod/metrics", false)]
    [InlineData("audit", false)]              // the audit topic stays viewer-gated
    [InlineData("servers/factorio/console", false)]
    public void RequiresOperator_GatesOnlyHostLogs(string topic, bool expected) =>
        Assert.Equal(expected, StreamProtocol.RequiresOperator(topic));

    /// <summary>
    /// The per-topic gate, and its fail-closed default: a topic name this build has never heard of
    /// answers the viewer floor, so a caller holding nothing reaches only the one topic named as
    /// needing nothing.
    /// </summary>
    [Theory]
    [InlineData("hosts/hotrod/logs", KgsmTier.Operator)]
    [InlineData("hosts/hotrod/services", KgsmTier.Operator)]
    [InlineData("me", KgsmTier.None)]
    [InlineData("servers", KgsmTier.Viewer)]
    [InlineData("audit", KgsmTier.Viewer)]
    [InlineData("something-this-build-has-never-heard-of", KgsmTier.Viewer)]
    public void MinimumTier_GatesEachTopic(string topic, KgsmTier expected) =>
        Assert.Equal(expected, StreamProtocol.MinimumTier(topic));

    [Fact]
    public void HostLogEntityKey_IsUniquePerCursor()
    {
        // Unique per line (the audit/console precedent), NOT supersede-by-latest — distinct lines never collapse.
        Assert.NotEqual(StreamProtocol.HostLogEntityKey("s=abc;i=1"), StreamProtocol.HostLogEntityKey("s=abc;i=2"));
        Assert.Equal("logs:s=abc;i=1", StreamProtocol.HostLogEntityKey("s=abc;i=1"));
    }
}
