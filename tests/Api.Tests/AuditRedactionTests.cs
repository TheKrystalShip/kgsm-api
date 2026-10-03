using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// What an audit row looks like to a reader without <c>api:audit.personal-fields</c>.
/// </summary>
/// <remarks>
/// The property under all of it: the trail is the same length whoever reads it. A reader without the
/// action sees every row a reader with it does — that somebody was banned, that a command was run —
/// and only the values inside a row differ. Two people reading the same feed and being told a different history is the
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
    /// to whoever holds the personal-fields action. Their in-game name is not the same fact and stays —
    /// a roster that named nobody would answer nobody's question.
    /// </summary>
    [Fact]
    public void APlayersAddressNeedsPersonalFields_TheirNameDoesNot()
    {
        AuditRecord reader = AuditRedaction.Redacted(Row(
            "player.joined", "bob joined mc",
            ("playerName", "bob"), ("playerAddr", "95.49.44.91"), ("sessionKey", "abc")));

        Assert.False(reader.Meta!.ContainsKey("playerAddr"));
        Assert.Equal("bob", reader.Meta["playerName"]);
        Assert.Equal("abc", reader.Meta["sessionKey"]);
        Assert.Equal("bob joined mc", reader.Summary);
    }

    /// <summary>
    /// <b>Stripping the meta is not enough on its own.</b> A console row prints what was typed in its
    /// own sentence, so a redaction that only emptied <c>meta</c> would leave the command in the line
    /// above it — the value withheld and published at the same time.
    /// </summary>
    [Fact]
    public void AConsoleCommandLeavesTheSummaryTooNotJustTheMeta()
    {
        AuditRecord reader = AuditRedaction.Redacted(Row(
            "console.input.sent", "ran 'op somebody' on mc", ("command", "op somebody")));

        Assert.Null(reader.Meta);
        Assert.DoesNotContain("op somebody", reader.Summary, StringComparison.Ordinal);
        Assert.Equal("sent a console command to mc", reader.Summary);
    }

    /// <summary>
    /// The sentence a reader reads is the one the mapper itself writes for an event that carried no
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

        AuditRecord reader = AuditRedaction.Redacted(
            Row("console.input.sent", carried.Summary, ("command", "op somebody")));

        Assert.Equal(carriedNothing.Summary, reader.Summary);
    }

    /// <summary>
    /// A moderation target may be a name or an address and <em>the event does not say which</em> — the
    /// game's blueprint does. The catalog calls that conditional and tells a consumer that cannot
    /// resolve it to treat it as personal; this surface cannot, so a reader is told a ban happened
    /// without being told whose address it might be.
    /// </summary>
    [Fact]
    public void AModerationTargetNeedsOperatorBecauseItMayBeAnAddress()
    {
        AuditRecord reader = AuditRedaction.Redacted(Row(
            "player.banned", "banned 95.49.44.91 on mc",
            ("target", "95.49.44.91"), ("command", "/ban 95.49.44.91")));

        Assert.Null(reader.Meta);
        Assert.Equal("banned a player on mc", reader.Summary);
        Assert.DoesNotContain("95.49", reader.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The row is never withheld, only values on it.</b> Every action still appears, with its
    /// timestamp, its actor and its server intact — an audit feed that showed a reader fewer rows
    /// would be telling two people different histories of the same host.
    /// </summary>
    [Fact]
    public void TheRowSurvivesEverythingTakenOffIt()
    {
        AuditRecord full = Row("player.banned", "banned bob on mc", ("target", "bob"));
        AuditRecord reader = AuditRedaction.Redacted(full);

        Assert.Equal(full.Id, reader.Id);
        Assert.Equal(full.Ts, reader.Ts);
        Assert.Equal(full.Action, reader.Action);
        Assert.Equal(full.Actor, reader.Actor);
        Assert.Equal(full.Origin, reader.Origin);
        Assert.Equal(full.Severity, reader.Severity);
        Assert.Equal(full.ServerId, reader.ServerId);
        Assert.Equal(full.Target, reader.Target);
    }

    /// <summary>A row carrying nothing restricted is handed back as it came, identity included.</summary>
    [Fact]
    public void AnOrdinaryRowIsNotCopiedAtAll()
    {
        AuditRecord row = Row("server.started", "started mc");

        Assert.Same(row, AuditRedaction.Redacted(row));
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
