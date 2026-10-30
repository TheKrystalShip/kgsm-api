using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The rows this API's own journal events become, now that it records what it did rather than writing
/// finished rows to a table.
/// </summary>
/// <remarks>
/// These are the safety net for the move. The wording, the severity, the actor and the meta keys are
/// what a reader has been seeing, and none of them may change just because the fact now arrives on a
/// different path — a sign-in that reads differently after the migration is a regression whatever the
/// plumbing underneath says.
/// <para>
/// They also pin the things the record deliberately does NOT carry. A payload that grows a password, a
/// config value or a file's contents is not a wording bug, and the tests that would catch it have to
/// assert absence rather than shape.
/// </para>
/// </remarks>
public sealed class ApiEventMappingTests
{
    private const string HostId = "hotrod";
    private static readonly DateTimeOffset When = DateTimeOffset.Parse("2026-08-12T10:00:00Z");

    // ---- provenance ---------------------------------------------------------------------------

    [Fact]
    public void A_row_takes_its_time_actor_and_origin_from_the_envelope()
    {
        // All three ride on the envelope rather than inside the payload — the journal reader stamps them
        // before a mapper sees them. A mapper reaching for DateTimeOffset.UtcNow would date the row when
        // it was READ, which for history is whenever somebody happened to open the page.
        AuditWrite row = AuditMapping.FromServiceConfigEvent(new ServiceConfigChangedEventData
        {
            Timestamp = When,
            Actor = "local:haru",
            Origin = AuditOrigin.Ui,
            Leaf = "monitor",
            DisplayName = "Monitor",
            Keys = ["intervalMs"],
            Outcome = "applied",
        }, HostId);

        Assert.Equal(When, row.Ts);
        Assert.Equal("haru", row.Actor.Name);
        Assert.Equal(ActorKind.User, row.Actor.Kind);
        Assert.Equal(AuditOrigin.Ui, row.Origin);
    }

    // ---- leaf configuration --------------------------------------------------------------------

    [Fact]
    public void A_config_change_carries_the_keys_and_never_a_value()
    {
        // The one mapping that would leak a credential if it were wrong: a leaf's configuration holds
        // API keys, bot tokens and webhook URLs.
        AuditWrite row = AuditMapping.FromServiceConfigEvent(new ServiceConfigChangedEventData
        {
            Timestamp = When,
            Actor = "discord:haru",
            Origin = AuditOrigin.Api,
            Leaf = "monitor",
            DisplayName = "Monitor",
            Keys = ["intervalMs", "apiKey"],
            Outcome = "applied",
        }, HostId);

        Assert.Equal("service.config_changed", row.Action);
        Assert.Equal("intervalMs,apiKey", row.Meta!["keys"]);
        Assert.DoesNotContain(row.Meta, kv => kv.Key.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_change_that_severed_the_link_to_the_leaf_says_so()
    {
        // applied_unreachable is a real outcome: the leaf restarted perfectly and this API can no longer
        // reach it. Reporting it as a plain success would be the one case nobody goes looking for.
        AuditWrite row = AuditMapping.FromServiceConfigEvent(new ServiceConfigChangedEventData
        {
            Timestamp = When,
            Actor = "discord:haru",
            Leaf = "monitor",
            DisplayName = "Monitor",
            Keys = ["socket"],
            Outcome = "applied_unreachable",
        }, HostId);

        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Contains("no longer reach it", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_restart_is_recorded_as_loudly_as_a_performed_one()
    {
        // Exactly the case nobody was watching a screen for.
        ServiceRestartedEventData Restart(bool ok) => new()
        {
            Timestamp = When,
            Actor = "discord:haru",
            Origin = AuditOrigin.Notification,
            Leaf = "monitor",
            DisplayName = "Monitor",
            Unit = "kgsm-monitor.service",
            Ok = ok,
        };

        Assert.Equal(AuditSeverity.Warn, AuditMapping.FromServiceRestartedEvent(Restart(true), HostId).Severity);

        AuditWrite refused = AuditMapping.FromServiceRestartedEvent(Restart(false), HostId);
        Assert.Equal(AuditSeverity.Danger, refused.Severity);
        Assert.Contains("systemd refused", refused.Summary, StringComparison.Ordinal);
        Assert.Equal("false", refused.Meta!["ok"]);
    }

    // ---- instance-scoped panel actions ---------------------------------------------------------

    [Fact]
    public void A_file_write_identifies_the_bytes_and_carries_none_of_them()
    {
        // An instance's config files hold rcon passwords, tokens and webhook URLs.
        AuditWrite row = AuditMapping.FromFileWrittenEvent(new FileWrittenEventData
        {
            Timestamp = When,
            Actor = "discord:haru",
            Origin = AuditOrigin.Ui,
            InstanceName = "factorio-1",
            Path = "config/server-settings.json",
            SizeBytes = 2048,
            Sha256 = "sha256:abc",
        }, HostId);

        Assert.Equal("file.written", row.Action);
        Assert.Equal("factorio-1", row.ServerId);
        Assert.Equal("config/server-settings.json", row.Meta!["path"]);
        Assert.Equal("2048", row.Meta["sizeBytes"]);
        Assert.Equal("sha256:abc", row.Meta["sha256"]);
        Assert.DoesNotContain(row.Meta, kv =>
            kv.Key.Contains("content", StringComparison.OrdinalIgnoreCase)
            || kv.Key.Contains("body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_backup_leaving_the_host_is_not_routine()
    {
        // A copy of somebody's world left this machine. It is scoped to the server so it shows on that
        // server's own history, and it is warn rather than info because it is worth noticing.
        AuditWrite row = AuditMapping.FromBackupDownloadedEvent(new BackupDownloadedEventData
        {
            Timestamp = When,
            Actor = "discord:haru",
            Origin = AuditOrigin.Ui,
            InstanceName = "factorio-1",
            BackupId = "backup-3",
            SizeBytes = 999,
        }, HostId);

        Assert.Equal("backup.downloaded", row.Action);
        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Equal("factorio-1", row.ServerId);
        Assert.Equal("backup-3", row.Meta!["source"]);
    }

    // ---- the classification the redactor reads --------------------------------------------------

    [Fact]
    public void The_values_that_identify_a_person_are_withheld_without_the_personal_fields_action()
    {
        // AuditRedaction builds its restricted set from the catalog's field sensitivities, so this
        // asserts the two ends meet: classified Personal upstream, actually withheld here.
        Assert.True(AuditRedaction.IsRestricted("identity"));
        Assert.True(AuditRedaction.IsRestricted("userAgent"));

        // And the counterweight — a trail that recorded accounts changing and named nobody would not
        // be a safer log.
        Assert.False(AuditRedaction.IsRestricted("username"));
        Assert.False(AuditRedaction.IsRestricted("sid"));
    }

    [Fact]
    public void A_reader_without_personal_fields_keeps_the_row_and_loses_only_the_personal_values()
    {
        AuditRecord full = new(
            Id: "evt_1", Ts: When, Origin: AuditOrigin.Ui, Actor: new AuditActor(ActorKind.User, "haru", ActorProvider.Local),
            Action: "auth.signed_in", Severity: AuditSeverity.Info, Target: null, ServerId: null, HostId: HostId,
            Summary: "haru signed in",
            Meta: new Dictionary<string, string>
            {
                ["userAgent"] = "Firefox/1.0",
                ["identity"] = "local:haru",
                ["sid"] = "sid_1",
            });

        AuditRecord seen = AuditRedaction.Redacted(full);

        // Same fact, same id, same moment — a shorter feed for one reader would be two people reading
        // one host's history and being told different things.
        Assert.Equal(full.Id, seen.Id);
        Assert.Equal(full.Action, seen.Action);
        Assert.Equal(full.Ts, seen.Ts);

        Assert.False(seen.Meta!.ContainsKey("userAgent"));
        Assert.False(seen.Meta.ContainsKey("identity"));
        Assert.Equal("sid_1", seen.Meta["sid"]);
    }
}
