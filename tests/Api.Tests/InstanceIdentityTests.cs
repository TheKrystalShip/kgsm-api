using System.Text.Json;
using TheKrystalShip.Api.Contracts;
using TheKrystalShip.Api.Services.Audit;
using TheKrystalShip.Api.Services.Commands;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The two pure halves of the id/label split: the slug a create form's free text is offered to the
/// engine as, and the audit row a rename produces. Both are I/O-free, so they are asserted directly
/// rather than through the pipeline.
/// </summary>
public sealed class InstanceIdentityTests
{
    // --- InstanceIdSlug: a candidate id, never the answer -------------------------------------------

    [Theory]
    [InlineData("Sunday Server", "sunday-server")]
    [InlineData("My  Server!!", "my-server")]
    [InlineData("  Trimmed  ", "trimmed")]
    [InlineData("factorio", "factorio")]
    [InlineData("Ketchup 2.0", "ketchup-2-0")]
    public void Slug_ReducesALabelToTheIdCharset(string label, string expected)
    {
        Assert.Equal(expected, InstanceIdSlug.From(label));
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("日曜サーバー")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Slug_IsNullWhenTheLabelYieldsNothingUsable(string? label)
    {
        // Null means "let the engine mint one". A placeholder would name a server after nothing anybody
        // typed, which is the fabrication this whole API avoids.
        Assert.Null(InstanceIdSlug.From(label));
    }

    [Fact]
    public void Slug_StartsAlphanumeric_AndFitsTheEngineLimit()
    {
        // The engine's charset is ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ — a leading separator would fail it,
        // and so would a 65th character.
        string? leading = InstanceIdSlug.From("---leading dashes");
        Assert.Equal("leading-dashes", leading);

        string? long_ = InstanceIdSlug.From(new string('a', 200));
        Assert.NotNull(long_);
        Assert.Equal(InstanceIdSlug.MaxLength, long_!.Length);
    }

    // --- server.rename: the row a rename leaves behind ---------------------------------------------

    [Fact]
    public void RenameRow_KeysOnTheIdAndNamesBothLabels()
    {
        AuditWrite w = AuditMapping.FromDisplayNameChangedEvent(new InstanceDisplayNameChangedData
        {
            InstanceName = "factorio-42",
            OldDisplayName = "Typo Proof",
            NewDisplayName = "Sunday Server",
            Actor = "discord:haru",
            Origin = "ui",
        }, "hotrod");

        Assert.Equal("server.renamed", w.Action);
        Assert.Equal(AuditSeverity.Info, w.Severity);
        // Immutable identity: the rename did not touch the id, so every earlier row still joins to it.
        Assert.Equal("factorio-42", w.ServerId);
        Assert.Equal("factorio-42", w.Target.Id);
        Assert.Equal("haru", w.Actor.Name);
        Assert.Equal("ui", w.Origin);
        Assert.Equal("Typo Proof", w.Meta!["oldDisplayName"]);
        Assert.Equal("Sunday Server", w.Meta!["newDisplayName"]);
        Assert.Contains("Typo Proof", w.Summary);
        Assert.Contains("Sunday Server", w.Summary);
    }

    [Fact]
    public void RenameRow_ReportsAClearedLabelAsTheIdItNowReadsAs()
    {
        AuditWrite w = AuditMapping.FromDisplayNameChangedEvent(new InstanceDisplayNameChangedData
        {
            InstanceName = "factorio-42",
            OldDisplayName = "Sunday Server",
            NewDisplayName = "",
        }, "hotrod");

        Assert.Equal("factorio-42", w.Meta!["newDisplayName"]);
    }

    [Fact]
    public void ConfigChange_ForTheLabelKey_IsDroppedInFavourOfTheRenameRow()
    {
        // The engine writes display_name and emits BOTH a config.changed naming the key and the
        // richer server.renamed. Shaping the first as well would file one rename as two
        // rows, one of which cannot say what changed.
        Assert.Null(EngineEventShaping.Shape(ConfigChange("display_name"), "hotrod"));

        // An ordinary config key still shapes — the drop is one key, not a hole in the config trail.
        Assert.NotNull(EngineEventShaping.Shape(ConfigChange("auto_update"), "hotrod"));
    }

    private static EventHistoryEntry ConfigChange(string key) => new(
        Id: $"evt_test_0_{key}",
        Ts: DateTimeOffset.UtcNow,
        Type: "config.changed",
        Instance: "factorio-42",
        Blueprint: null,
        Actor: "discord:haru",
        Origin: "ui",
        Hostname: null,
        Data: JsonSerializer.SerializeToElement(new { InstanceName = "factorio-42", Key = key }));
}
