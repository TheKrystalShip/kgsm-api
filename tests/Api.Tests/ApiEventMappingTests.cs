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

    private static AuthSessionEventData Session(
        string provider = "discord", string? peerNode = null, string? userAgent = null) => new()
        {
            Timestamp = When,
            Actor = $"{provider}:haru",
            Origin = AuditOrigin.Ui,
            UserId = "usr_abc",
            Username = "haru",
            Identity = $"{provider}:haru",
            Provider = provider,
            Tier = "operator",
            Sid = "sid_1",
            UserAgent = userAgent,
            PeerNode = peerNode,
        };

    private static UserAccountEventData Account(
        string? fromTier = null, string? toTier = null, string? fromStatus = null,
        string? toStatus = null, bool? byHolder = null) => new()
        {
            Timestamp = When,
            Actor = "discord:admin",
            Origin = AuditOrigin.Ui,
            UserId = "usr_target",
            Username = "someone",
            FromTier = fromTier,
            ToTier = toTier,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ByHolder = byHolder,
        };

    // ---- provenance ---------------------------------------------------------------------------

    [Fact]
    public void A_row_takes_its_time_actor_and_origin_from_the_envelope()
    {
        // All three ride on the envelope rather than inside the payload — the journal reader stamps them
        // before a mapper sees them. A mapper reaching for DateTimeOffset.UtcNow would date the row when
        // it was READ, which for history is whenever somebody happened to open the page.
        AuditWrite row = AuditMapping.FromAuthSessionEvent(Session(), ApiJournal.LoginEvent, HostId);

        Assert.Equal(When, row.Ts);
        Assert.Equal("haru", row.Actor.Name);
        Assert.Equal(ActorKind.User, row.Actor.Kind);
        Assert.Equal(AuditOrigin.Ui, row.Origin);
    }

    // ---- sessions -----------------------------------------------------------------------------

    [Fact]
    public void A_password_sign_in_and_a_provider_sign_in_read_differently()
    {
        // Told apart by the PROVIDER on the record, not by which endpoint ran. That is what lets both
        // sentences come from one stored fact — and what makes the distinction survive into history.
        Assert.Contains("signed in with a password",
            AuditMapping.FromAuthSessionEvent(Session(provider: "local"), ApiJournal.LoginEvent, HostId).Summary,
            StringComparison.Ordinal);

        Assert.Contains("logged in",
            AuditMapping.FromAuthSessionEvent(Session(provider: "discord"), ApiJournal.LoginEvent, HostId).Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_vouched_session_names_the_node_that_asserted_it()
    {
        AuditWrite row = AuditMapping.FromAuthSessionEvent(
            Session(peerNode: "node-b"), ApiJournal.ClusterSessionEvent, HostId);

        Assert.Equal(AuditAction.AuthClusterSession, row.Action);
        Assert.Contains("node-b", row.Summary, StringComparison.Ordinal);
        Assert.Equal("node-b", row.Meta!["peerNode"]);
    }

    [Fact]
    public void A_login_carries_the_session_it_opened()
    {
        // The pairing a logout is matched against, and the whole reason a revocation can be traced to
        // the sign-in that created it.
        AuditWrite row = AuditMapping.FromAuthSessionEvent(Session(), ApiJournal.LoginEvent, HostId);

        Assert.Equal("sid_1", row.Meta!["sid"]);
        Assert.Equal("operator", row.Meta["tier"]);
    }

    [Fact]
    public void A_device_appears_only_when_one_was_sent()
    {
        Assert.False(AuditMapping.FromAuthSessionEvent(Session(), ApiJournal.LoginEvent, HostId)
            .Meta!.ContainsKey("userAgent"));

        Assert.Equal("Firefox/1.0", AuditMapping
            .FromAuthSessionEvent(Session(userAgent: "Firefox/1.0"), ApiJournal.LoginEvent, HostId)
            .Meta!["userAgent"]);
    }

    // ---- revocations --------------------------------------------------------------------------

    [Theory]
    [InlineData("self", AuditAction.AuthSessionRevoke, AuditSeverity.Info)]
    [InlineData("all", AuditAction.AuthSessionRevokeAll, AuditSeverity.Info)]
    // An admin ending somebody else's session is the substantial-power case; a person managing their
    // own is routine, and a trail that shouted equally about both would be no easier to read.
    [InlineData("admin", AuditAction.AuthSessionRevokeAdmin, AuditSeverity.Warn)]
    public void A_revocation_maps_its_scope_to_an_action_and_a_weight(
        string scope, string action, string severity)
    {
        AuditWrite row = AuditMapping.FromSessionRevokedEvent(new AuthSessionRevokedData
        {
            Timestamp = When,
            Actor = "discord:haru",
            Origin = AuditOrigin.Ui,
            Scope = scope,
            UserId = "usr_target",
            Username = "someone",
            Sid = "sid_1",
            Count = 1,
        }, HostId);

        Assert.Equal(action, row.Action);
        Assert.Equal(severity, row.Severity);
    }

    [Fact]
    public void An_admin_revocation_says_whose_sessions_ended_not_only_who_ended_them()
    {
        // The two are different people on exactly the rows where it matters. Collapsing them would make
        // "who was logged out" unanswerable for every admin action.
        AuditWrite row = AuditMapping.FromSessionRevokedEvent(new AuthSessionRevokedData
        {
            Timestamp = When,
            Actor = "discord:admin",
            Origin = AuditOrigin.Ui,
            Scope = "admin",
            UserId = "usr_target",
            Username = "someone",
            Count = 3,
        }, HostId);

        Assert.Equal("admin", row.Actor.Name);
        Assert.Contains("someone", row.Summary, StringComparison.Ordinal);
        Assert.Equal("3", row.Meta!["count"]);
    }

    // ---- accounts -----------------------------------------------------------------------------

    [Fact]
    public void A_tier_change_records_both_ends_of_the_move()
    {
        // With the account store as this host's sole authority, this row is the ONLY record that
        // anybody's permissions ever moved.
        AuditWrite row = AuditMapping.FromUserAccountEvent(
            Account(fromTier: "viewer", toTier: "admin"), ApiJournal.UserTierChangedEvent, HostId);

        Assert.Equal(AuditAction.UserTierChange, row.Action);
        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Equal("viewer", row.Meta!["fromTier"]);
        Assert.Equal("admin", row.Meta["toTier"]);
        Assert.Contains("viewer", row.Summary, StringComparison.Ordinal);
        Assert.Contains("admin", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Being_switched_off_and_being_sent_back_for_approval_are_different_sentences()
    {
        // One event, told apart by where the account LANDED. A verb chosen at write time would have
        // frozen the distinction into the record, and getting it wrong there is unfixable.
        AuditWrite disabled = AuditMapping.FromUserAccountEvent(
            Account(fromStatus: "active", toStatus: "disabled"), ApiJournal.UserDisabledEvent, HostId);
        AuditWrite pending = AuditMapping.FromUserAccountEvent(
            Account(fromStatus: "active", toStatus: "pending"), ApiJournal.UserDisabledEvent, HostId);

        Assert.Contains("disabled the account", disabled.Summary, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Danger, disabled.Severity);

        Assert.Contains("awaiting approval", pending.Summary, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warn, pending.Severity);
    }

    [Fact]
    public void A_password_set_by_somebody_else_reads_differently_from_your_own()
    {
        // The only signal an account takeover leaves. A row that could not tell the two apart would
        // report the takeover and the routine rotation identically.
        AuditWrite mine = AuditMapping.FromUserAccountEvent(
            Account(byHolder: true), ApiJournal.UserPasswordChangedEvent, HostId);
        AuditWrite theirs = AuditMapping.FromUserAccountEvent(
            Account(byHolder: false), ApiJournal.UserPasswordChangedEvent, HostId);

        Assert.Contains("their own password", mine.Summary, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Info, mine.Severity);
        Assert.Equal("self", mine.Meta!["by"]);

        Assert.Contains("set the password on", theirs.Summary, StringComparison.Ordinal);
        Assert.Equal(AuditSeverity.Warn, theirs.Severity);
        Assert.Equal("admin", theirs.Meta!["by"]);
    }

    [Fact]
    public void A_provision_invents_no_state_it_moved_out_of()
    {
        // An account did not exist a moment ago, so there is no "from". A from/to pair here would be a
        // previous state nobody was ever in.
        AuditWrite row = AuditMapping.FromUserAccountEvent(
            Account(toTier: "viewer", toStatus: "pending"), ApiJournal.UserProvisionedEvent, HostId);

        Assert.False(row.Meta!.ContainsKey("fromTier"));
        Assert.False(row.Meta.ContainsKey("from"));
        Assert.Equal("viewer", row.Meta["toTier"]);
    }

    [Fact]
    public void An_account_row_names_the_admin_as_actor_and_the_account_as_target()
    {
        AuditWrite row = AuditMapping.FromUserAccountEvent(
            Account(toStatus: "active"), ApiJournal.UserApprovedEvent, HostId);

        Assert.Equal("admin", row.Actor.Name);           // who did it
        Assert.Equal("usr_target", row.Target!.Id);      // to whom
        Assert.Equal("someone", row.Meta!["username"]);
    }

    // ---- leaf services ------------------------------------------------------------------------

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

        Assert.Equal(AuditAction.ServiceConfig, row.Action);
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

        Assert.Equal(AuditAction.FileWrite, row.Action);
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

        Assert.Equal(AuditAction.BackupDownload, row.Action);
        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Equal("factorio-1", row.ServerId);
        Assert.Equal("backup-3", row.Meta!["source"]);
    }

    // ---- the classification the redactor reads --------------------------------------------------

    [Fact]
    public void The_values_that_identify_a_person_are_withheld_below_operator()
    {
        // AuditRedaction builds its restricted set from the catalog's field sensitivities, so this
        // asserts the two ends meet: classified Personal upstream, actually withheld here.
        Assert.True(AuditRedaction.IsRestricted("identity"));
        Assert.True(AuditRedaction.IsRestricted("userAgent"));

        // And the counterweight — a trail that recorded authority changing and named nobody would not
        // be a safer log.
        Assert.False(AuditRedaction.IsRestricted("username"));
        Assert.False(AuditRedaction.IsRestricted("tier"));
    }

    [Fact]
    public void A_viewer_keeps_the_row_and_loses_only_the_personal_values()
    {
        AuditWrite write = AuditMapping.FromAuthSessionEvent(
            Session(userAgent: "Firefox/1.0"), ApiJournal.LoginEvent, HostId);
        AuditRecord full = AuditMapping.ToRecordDirect(write, "evt_1");

        AuditRecord seen = AuditRedaction.ForViewer(full);

        // Same fact, same id, same moment — a shorter feed for one tier would be two people reading one
        // host's history and being told different things.
        Assert.Equal(full.Id, seen.Id);
        Assert.Equal(full.Action, seen.Action);
        Assert.Equal(full.Ts, seen.Ts);

        Assert.False(seen.Meta!.ContainsKey("userAgent"));
        Assert.False(seen.Meta.ContainsKey("identity"));
        Assert.Equal("operator", seen.Meta["tier"]);
    }
}
