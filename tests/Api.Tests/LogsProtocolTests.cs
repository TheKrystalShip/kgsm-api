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

    /// <summary>
    /// The per-topic gate, and its fail-closed default: a topic name this build has never heard of is an
    /// Owner's alone, so a caller holding nothing reaches only the one topic named as needing nothing.
    /// </summary>
    [Theory]
    [InlineData("hosts/hotrod/logs", TopicGateKind.Node, "api:logs.read", null)]
    [InlineData("hosts/hotrod/services", TopicGateKind.Node, "api:services.read", null)]
    [InlineData("hosts/hotrod/metrics", TopicGateKind.Node, "monitor:metrics.read", null)]
    [InlineData("hosts/hotrod/capabilities", TopicGateKind.Node, "api:hosts.read", null)]
    [InlineData("audit", TopicGateKind.Node, "api:audit.read", null)]
    [InlineData("alerts", TopicGateKind.Node, "api:alerts.read", null)]
    [InlineData("batches", TopicGateKind.Node, "api:batches.read", null)]
    [InlineData("me", TopicGateKind.Open, null, null)]
    [InlineData("servers", TopicGateKind.PerServer, "kgsm:server.read", null)]
    [InlineData("servers/metrics", TopicGateKind.PerServer, "kgsm:server.read", null)]
    [InlineData("jobs", TopicGateKind.PerServer, "kgsm:server.read", null)]
    [InlineData("players", TopicGateKind.PerServer, "kgsm:server.read", null)]
    [InlineData("servers/factorio/metrics", TopicGateKind.Server, "kgsm:server.read", "factorio")]
    [InlineData("servers/factorio/console", TopicGateKind.Server, "kgsm:server.console.read", "factorio")]
    [InlineData("something-this-build-has-never-heard-of", TopicGateKind.OwnerOnly, null, null)]
    public void Gate_NamesEachTopicsAction(string topic, TopicGateKind kind, string? action, string? serverId) =>
        Assert.Equal(new TopicGate(kind, action, serverId), StreamProtocol.Gate(topic));

    [Fact]
    public void HostLogEntityKey_IsUniquePerCursor()
    {
        // Unique per line (the audit/console precedent), NOT supersede-by-latest — distinct lines never collapse.
        Assert.NotEqual(StreamProtocol.HostLogEntityKey("s=abc;i=1"), StreamProtocol.HostLogEntityKey("s=abc;i=2"));
        Assert.Equal("logs:s=abc;i=1", StreamProtocol.HostLogEntityKey("s=abc;i=1"));
    }
}
