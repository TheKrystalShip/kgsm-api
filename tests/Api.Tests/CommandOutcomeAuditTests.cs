using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Commands;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The rows a command that did NOT do the thing becomes.
/// </summary>
/// <remarks>
/// This is the one part of the write path with no engine event behind it: kgsm emits an event when a
/// verb works, and a verb that fails, is refused, or never runs exits non-zero and says nothing. These
/// pin the three things that makes correct — that a capacity refusal never reads as a fault, that the
/// engine's own words and number are carried rather than reworded, and that the two verbs whose failure
/// the engine reports itself are left alone.
/// </remarks>
public sealed class CommandOutcomeAuditTests
{
    private const string HostId = "hotrod";
    private static readonly DateTimeOffset When = DateTimeOffset.Parse("2026-08-22T10:00:00Z");

    private static CommandOutcomeEventData Outcome(
        string verb = CommandVerb.Start, string? error = null, int? exitCode = null,
        string? batchId = null, string actor = "discord:haru", string origin = AuditOrigin.Ui) => new()
        {
            Timestamp = When,
            Actor = actor,
            Origin = origin,
            InstanceName = "factorio-test",
            Verb = verb,
            JobId = "job_abc123",
            BatchId = batchId,
            Error = error,
            ExitCode = exitCode,
        };

    // ---- the three outcomes are three different facts -------------------------------------------

    [Fact]
    public void A_failure_is_danger_and_names_what_did_not_happen()
    {
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(error: "kgsm: no such instance", exitCode: 2),
            ApiJournal.CommandFailedEvent, HostId);

        Assert.Equal(AuditAction.CommandFailed, row.Action);
        Assert.Equal(AuditSeverity.Danger, row.Severity);
        Assert.Equal("could not start factorio-test", row.Summary);
        Assert.Equal("factorio-test", row.ServerId);
    }

    [Fact]
    public void A_capacity_refusal_is_not_a_failure()
    {
        // The distinction the engine's exit code exists for: nothing is wrong with this instance, the
        // node was full. Filing it as danger blames the server, and a retry policy reading it as a fault
        // re-issues a command certain to be refused identically until something else stops.
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(error: "not enough free memory: needs 4096MB, 900MB free",
                    exitCode: 51),
            ApiJournal.CommandRefusedEvent, HostId);

        Assert.Equal(AuditAction.CommandRefused, row.Action);
        Assert.Equal(AuditSeverity.Warn, row.Severity);
        Assert.Contains("refused to start factorio-test", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancellation_is_routine_and_says_nothing_ran()
    {
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(verb: CommandVerb.Update, batchId: "batch_dead1"),
            ApiJournal.CommandCancelledEvent, HostId);

        Assert.Equal(AuditAction.CommandCancelled, row.Action);
        Assert.Equal(AuditSeverity.Info, row.Severity);
        Assert.Contains("cancelled before it ran", row.Summary, StringComparison.Ordinal);
        Assert.Equal("batch_dead1", row.Meta!["batchId"]);
    }

    // ---- what the row carries -------------------------------------------------------------------

    [Fact]
    public void The_engines_own_words_and_number_are_carried_not_reworded()
    {
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(error: "steamcmd exited 8", exitCode: 8), ApiJournal.CommandFailedEvent, HostId);

        Assert.Equal("steamcmd exited 8", row.Meta!["error"]);
        Assert.Equal("8", row.Meta["exitCode"]);

        // The sentence says what did not happen and nothing else, so a reworded kgsm message changes
        // what a reader can dig into and not how the feed reads.
        Assert.DoesNotContain("steamcmd", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_said_nothing_gains_no_detail()
    {
        // Never fabricate: the engine was never reached, so there is no exit code and no message, and
        // the row says so by carrying neither.
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(), ApiJournal.CommandFailedEvent, HostId);

        Assert.False(row.Meta!.ContainsKey("error"));
        Assert.False(row.Meta.ContainsKey("exitCode"));
        Assert.False(row.Meta.ContainsKey("batchId"));
    }

    [Fact]
    public void The_job_is_named_because_this_producer_holds_it()
    {
        // No id round-trips the stateless engine, which is why an echo-sourced row carries none. This
        // row is written by the process that owns the job, so naming it reports what it holds.
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(), ApiJournal.CommandFailedEvent, HostId);

        Assert.Equal("job_abc123", row.Meta!["jobId"]);
        Assert.Equal(CommandVerb.Start, row.Meta["verb"]);
    }

    [Fact]
    public void Provenance_rides_the_envelope_and_is_never_invented()
    {
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(), ApiJournal.CommandFailedEvent, HostId);

        Assert.Equal(When, row.Ts);
        Assert.Equal("haru", row.Actor.Name);
        Assert.Equal(AuditOrigin.Ui, row.Origin);

        // A surface this API does not recognise loses its provenance rather than keeping a value the
        // closed vocabulary never defined.
        Assert.Null(AuditMapping
            .FromCommandOutcomeEvent(Outcome(origin: "carrier-pigeon"), ApiJournal.CommandFailedEvent, HostId)
            .Origin);
    }

    [Fact]
    public void A_verb_with_no_natural_phrasing_still_names_itself()
    {
        Assert.Equal("could not back up factorio-test",
            AuditMapping.FromCommandOutcomeEvent(
                Outcome(verb: CommandVerb.BackupCreate), ApiJournal.CommandFailedEvent, HostId).Summary);

        // A verb the sentence map does not know is printed as recorded. The only record of a failure
        // must be able to say what failed.
        Assert.Contains("defrag",
            AuditMapping.FromCommandOutcomeEvent(
                Outcome(verb: "defrag"), ApiJournal.CommandFailedEvent, HostId).Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_on_the_row_is_withheld_from_a_viewer()
    {
        // An engine failure message is diagnostic prose about this host's own operation, not about a
        // person and not a value the engine classifies as privileged — so the redactor has nothing to
        // take off. ⚠ The meta key is `verb`, deliberately not `command`, which the engine classifies as
        // privileged for the console-input event and which a name-keyed redactor would strip here too.
        AuditWrite write = AuditMapping.FromCommandOutcomeEvent(
            Outcome(error: "/opt/kgsm did not answer", exitCode: 1), ApiJournal.CommandFailedEvent, HostId);
        AuditRecord row = AuditMapping.ToRecordDirect(write, "evt_test");

        Assert.Equal(row, AuditRedaction.ForViewer(row));
    }

    // ---- the categorisation the runner makes ----------------------------------------------------

    [Fact]
    public void The_refusal_is_keyed_on_the_engines_exit_code_not_its_prose()
    {
        Assert.Equal(ApiJournal.CommandRefusedEvent, CommandRunner.OutcomeEvent(51));
        Assert.Equal(ApiJournal.CommandFailedEvent, CommandRunner.OutcomeEvent(1));
        Assert.Equal(ApiJournal.CommandFailedEvent, CommandRunner.OutcomeEvent(50));

        // The engine was never reached, so there is no number to read — a fault, not a refusal.
        Assert.Equal(ApiJournal.CommandFailedEvent, CommandRunner.OutcomeEvent(null));
    }

    [Fact]
    public void The_two_verbs_the_engine_reports_itself_are_left_to_it()
    {
        // ⚠ The no-double-write line. kgsm emits instance_update_failed and instance_uninstall_failed,
        // and both already become rows carrying the provenance the command stamped onto the call.
        Assert.True(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Update));
        Assert.True(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Uninstall));

        // Every other verb exits non-zero and emits nothing.
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Start));
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Stop));
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Restart));
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Install));
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.BackupCreate));
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.BackupRestore));
    }

    // ---- a move names the two disks it was between ----------------------------------------------

    [Fact]
    public void A_refused_move_names_both_libraries()
    {
        // The successful move is the engine's own instance_moved, which carries the same pair. This is
        // the half no producer records — the disk somebody was trying to empty, and the one it could not
        // be emptied onto — and it is what they come back to afterwards.
        CommandOutcomeEventData d = Outcome(
            verb: CommandVerb.Move, error: "not enough free space in library 'archive'", exitCode: 56);
        d.FromLibrary = "ssd";
        d.ToLibrary = "archive";

        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(d, ApiJournal.CommandFailedEvent, HostId);

        Assert.Equal("could not move factorio-test", row.Summary);
        Assert.Equal("ssd", row.Meta!["fromLibrary"]);
        Assert.Equal("archive", row.Meta["toLibrary"]);
        Assert.Equal("not enough free space in library 'archive'", row.Meta["error"]);
    }

    [Fact]
    public void A_verb_that_is_not_a_move_carries_neither_library()
    {
        AuditWrite row = AuditMapping.FromCommandOutcomeEvent(
            Outcome(error: "boom", exitCode: 1), ApiJournal.CommandFailedEvent, HostId);

        Assert.False(row.Meta!.ContainsKey("fromLibrary"));
        Assert.False(row.Meta.ContainsKey("toLibrary"));
    }

    [Fact]
    public void A_move_the_engine_never_reached_still_records_where_it_was_going()
    {
        // The engine reports a move's failure with no event of its own, so this row is the only record —
        // and one that could not say which disk was being emptied would be the row without its point.
        Assert.False(CommandRunner.EngineRecordsItsOwnFailure(CommandVerb.Move));
    }
}
