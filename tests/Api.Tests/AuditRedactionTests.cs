using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What an audit row looks like to a reader below operator.
/// </summary>
/// <remarks>
/// The property under all of it: the trail is the same length whoever reads it. A viewer sees every
/// row an operator does — that somebody was banned, that a command was run — and only the values
/// inside a row differ. Two people reading the same feed and being told a different history is the
/// failure this must not have.
/// </remarks>
public sealed class AuditRedactionTests
{
    private static readonly DateTimeOffset Ts = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static AuditRecord Row(
        string action, string summary, params (string Key, string Value)[] meta) =>
        new("evt_1", Ts, "system", new AuditActor("system", "system", "system"),
            action, AuditSeverity.Info, new AuditTarget(AuditTargetKind.Server, "mc", "mc"),
            "mc", "h1", summary,
            meta.Length == 0 ? null : meta.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal));

    /// <summary>
    /// The decision this implements: a player's connection address is shown on the Control Panel, and
    /// to operators. Their in-game name is not the same fact and stays — a roster that named nobody
    /// would answer nobody's question.
    /// </summary>
    [Fact]
    public void APlayersAddressNeedsOperator_TheirNameDoesNot()
    {
        AuditRecord viewer = AuditRedaction.ForViewer(Row(
            AuditAction.PlayerJoin, "bob joined mc",
            ("playerName", "bob"), ("playerAddr", "95.49.44.91"), ("sessionKey", "abc")));

        Assert.False(viewer.Meta!.ContainsKey("playerAddr"));
        Assert.Equal("bob", viewer.Meta["playerName"]);
        Assert.Equal("abc", viewer.Meta["sessionKey"]);
        Assert.Equal("bob joined mc", viewer.Summary);
    }

    /// <summary>
    /// <b>Stripping the meta is not enough on its own.</b> A console row prints what was typed in its
    /// own sentence, so a redaction that only emptied <c>meta</c> would leave the command in the line
    /// above it — the value withheld and published at the same time.
    /// </summary>
    [Fact]
    public void AConsoleCommandLeavesTheSummaryTooNotJustTheMeta()
    {
        AuditRecord viewer = AuditRedaction.ForViewer(Row(
            AuditAction.ConsoleInput, "ran 'op somebody' on mc", ("command", "op somebody")));

        Assert.Null(viewer.Meta);
        Assert.DoesNotContain("op somebody", viewer.Summary, StringComparison.Ordinal);
        Assert.Equal("sent a console command to mc", viewer.Summary);
    }

    /// <summary>
    /// The sentence a viewer reads is the one the mapper itself writes for an event that carried no
    /// command — the same function, not a second wording of it. That is what stops the two drifting
    /// the first time either is reworded.
    /// </summary>
    [Fact]
    public void TheWithheldSentenceIsTheOneTheMapperWritesWithoutTheValue()
    {
        AuditWrite carried = AuditMapping.FromInputSentEvent(
            new InstanceInputSentData { InstanceName = "mc", Command = "op somebody" }, "h1");
        AuditWrite carriedNothing = AuditMapping.FromInputSentEvent(
            new InstanceInputSentData { InstanceName = "mc", Command = "" }, "h1");

        AuditRecord viewer = AuditRedaction.ForViewer(
            Row(AuditAction.ConsoleInput, carried.Summary, ("command", "op somebody")));

        Assert.Equal(carriedNothing.Summary, viewer.Summary);
    }

    /// <summary>
    /// A moderation target may be a name or an address and <em>the event does not say which</em> — the
    /// game's blueprint does. The catalog calls that conditional and tells a consumer that cannot
    /// resolve it to treat it as personal; this surface cannot, so a viewer is told a ban happened
    /// without being told whose address it might be.
    /// </summary>
    [Fact]
    public void AModerationTargetNeedsOperatorBecauseItMayBeAnAddress()
    {
        AuditRecord viewer = AuditRedaction.ForViewer(Row(
            AuditAction.PlayerBan, "banned 95.49.44.91 on mc",
            ("target", "95.49.44.91"), ("command", "/ban 95.49.44.91")));

        Assert.Null(viewer.Meta);
        Assert.Equal("banned a player on mc", viewer.Summary);
        Assert.DoesNotContain("95.49", viewer.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The row is never withheld, only values on it.</b> Every action still appears, with its
    /// timestamp, its actor and its server intact — an audit feed that showed a viewer fewer rows
    /// would be telling two people different histories of the same host.
    /// </summary>
    [Fact]
    public void TheRowSurvivesEverythingTakenOffIt()
    {
        AuditRecord full = Row(AuditAction.PlayerBan, "banned bob on mc", ("target", "bob"));
        AuditRecord viewer = AuditRedaction.ForViewer(full);

        Assert.Equal(full.Id, viewer.Id);
        Assert.Equal(full.Ts, viewer.Ts);
        Assert.Equal(full.Action, viewer.Action);
        Assert.Equal(full.Actor, viewer.Actor);
        Assert.Equal(full.Origin, viewer.Origin);
        Assert.Equal(full.Severity, viewer.Severity);
        Assert.Equal(full.ServerId, viewer.ServerId);
        Assert.Equal(full.Target, viewer.Target);
    }

    /// <summary>A row carrying nothing restricted is handed back as it came, identity included.</summary>
    [Fact]
    public void AnOrdinaryRowIsNotCopiedAtAll()
    {
        AuditRecord row = Row(AuditAction.ServerStart, "started mc");

        Assert.Same(row, AuditRedaction.ForViewer(row));
    }

    /// <summary>
    /// The restricted set is the engine's classification, read by field name — which is sound because
    /// kgsm-lib classifies a given field name the same way on every event that carries it. Nothing
    /// public is ever caught by it.
    /// </summary>
    [Fact]
    public void TheRestrictedSetIsExactlyWhatTheEngineCallsNonPublic()
    {
        foreach (EventField field in KgsmEventCatalog.All.SelectMany(d => d.Fields))
        {
            bool restricted = AuditRedaction.IsRestricted(field.Name);

            Assert.Equal(field.Sensitivity != FieldSensitivity.Public, restricted);

            // The API writes meta keys camel-cased where kgsm names them Pascal-cased; the lookup has
            // to answer the same way for both or the strip silently misses every real row.
            Assert.Equal(restricted, AuditRedaction.IsRestricted(
                char.ToLowerInvariant(field.Name[0]) + field.Name[1..]));
        }
    }
}
