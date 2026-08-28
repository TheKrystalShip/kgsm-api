using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The rows a threshold episode becomes, now that kgsm-monitor records the fact in its own journal and
/// this API shapes it at read time.
/// </summary>
/// <remarks>
/// These are the safety net for the move. The wording, the severity, the target and the meta keys are
/// what a reader has been seeing, and none of them may change just because the fact now arrives on a
/// different path — a breach that reads differently after the migration is a regression whatever the
/// plumbing underneath says.
/// </remarks>
public sealed class ThresholdEventMappingTests
{
    private const string HostId = "hotrod";
    private static readonly long Opened = DateTimeOffset.Parse("2026-08-12T10:00:00Z").ToUnixTimeMilliseconds();
    private static readonly long Closed = DateTimeOffset.Parse("2026-08-12T10:02:30Z").ToUnixTimeMilliseconds();

    private static HostThresholdBreachedData Breach(string scope = "host", string? serverId = null,
        string? refKey = null, string band = "warn") => new()
        {
            EpisodeId = "ep-1",
            RuleKey = "host-mem",
            Metric = "memory",
            Scope = scope,
            Ref = refKey,
            ServerId = serverId,
            Threshold = 90,
            PeakValue = 96,
            PeakBand = band,
            OpenedTs = Opened,
            OpenValue = 95,
            Band = band,
        };

    private static HostThresholdClearedData Cleared(string? reason) => new()
    {
        EpisodeId = "ep-1",
        RuleKey = "host-mem",
        Metric = "memory",
        Scope = "host",
        Threshold = 90,
        PeakValue = 96,
        PeakBand = "warn",
        OpenedTs = Opened,
        ClosedTs = Closed,
        CloseValue = 41,
        CloseReason = reason,
    };

    [Fact]
    public void A_breach_is_timestamped_when_the_condition_changed()
    {
        // Not when the line was written. A reader scanning the trail has to see the breach where it
        // happened, and the payload carries the moment precisely so the envelope's own timestamp — which
        // says when it was recorded — never stands in for it.
        AuditWrite row = AuditMapping.FromThresholdBreachedEvent(Breach(), HostId);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Opened), row.Ts);
        Assert.Equal("host.threshold.breached", row.Action);
    }

    [Fact]
    public void A_breach_names_the_monitor_as_the_one_that_established_it()
    {
        AuditWrite row = AuditMapping.FromThresholdBreachedEvent(Breach(), HostId);

        // Not a bare "system": a log that could not tell a measured breach from any other unattended
        // action would leave nobody able to ask which component acted.
        Assert.Equal(ActorKind.System, row.Actor.Kind);
        Assert.Equal("monitor", row.Actor.Name);
        Assert.Equal(AuditOrigin.System, row.Origin);
    }

    [Fact]
    public void A_breach_is_as_loud_as_the_worst_band_it_reached()
    {
        // A condition that touched danger and eased back to warn was still a danger-band episode, so the
        // peak decides rather than the reading at either end.
        Assert.Equal(AuditSeverity.Danger,
            AuditMapping.FromThresholdBreachedEvent(Breach(band: "danger"), HostId).Severity);

        Assert.Equal(AuditSeverity.Warn,
            AuditMapping.FromThresholdBreachedEvent(Breach(band: "warn"), HostId).Severity);
    }

    [Fact]
    public void A_recovery_is_information_never_a_warning()
        => Assert.Equal(AuditSeverity.Info,
            AuditMapping.FromThresholdClearedEvent(Cleared("recovered"), HostId).Severity);

    [Theory]
    [InlineData("recovered", "back to normal")]
    // Neither of these recovered: the value was never observed to come down. Reporting them as a
    // return to normal would state a measurement nobody took.
    [InlineData("unwatched", "no longer watched")]
    [InlineData("interrupted", "still over its threshold when monitoring stopped")]
    public void A_clear_says_why_it_ended(string reason, string expected)
    {
        AuditWrite row = AuditMapping.FromThresholdClearedEvent(Cleared(reason), HostId);

        Assert.Contains(expected, row.Summary, StringComparison.Ordinal);
        Assert.Equal(reason, row.Meta!["reason"]);
    }

    [Fact]
    public void A_clear_reports_how_long_it_held()
    {
        AuditWrite row = AuditMapping.FromThresholdClearedEvent(Cleared("recovered"), HostId);

        Assert.Equal("150", row.Meta!["heldSec"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Closed), row.Ts);
    }

    [Fact]
    public void A_host_scoped_episode_targets_the_host_and_scopes_to_no_server()
    {
        AuditWrite row = AuditMapping.FromThresholdBreachedEvent(Breach(), HostId);

        Assert.Equal(AuditTargetKind.Host, row.Target!.Kind);
        Assert.Equal(HostId, row.Target.Id);
        // A host-wide condition belongs to no server, and filtering by one must not surface it.
        Assert.Null(row.ServerId);
    }

    [Fact]
    public void A_server_scoped_episode_targets_that_server()
    {
        AuditWrite row = AuditMapping.FromThresholdBreachedEvent(
            Breach(scope: "server", serverId: "factorio-1"), HostId);

        Assert.Equal(AuditTargetKind.Server, row.Target!.Kind);
        Assert.Equal("factorio-1", row.Target.Id);
        Assert.Equal("factorio-1", row.ServerId);
    }

    [Fact]
    public void The_subject_of_a_host_episode_is_what_was_measured_on()
    {
        // A disk rule names its mount; a rule with nothing further to name falls back to the host.
        Assert.StartsWith("/data", AuditMapping.FromThresholdBreachedEvent(
            Breach(refKey: "/data"), HostId).Summary, StringComparison.Ordinal);

        Assert.StartsWith(HostId, AuditMapping.FromThresholdBreachedEvent(
            Breach(), HostId).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_rows_carry_the_episode_they_belong_to()
    {
        // The pair is what lets a reader match a recovery to its breach; without it the two rows are
        // separate facts about nothing in particular.
        Assert.Equal("ep-1", AuditMapping.FromThresholdBreachedEvent(Breach(), HostId).Meta!["episodeId"]);
        Assert.Equal("ep-1", AuditMapping.FromThresholdClearedEvent(Cleared("recovered"), HostId).Meta!["episodeId"]);
    }
}
