using TheKrystalShip.Api.Services.Leaves;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The two judgements the rule-write path makes on its own: whether a body is a rules file at all, and
/// what the leaf said about it afterwards.
/// </summary>
/// <remarks>
/// ⚠ <b>Everything else is deliberately the leaf's.</b> Which signals, operators and actions exist
/// belongs to the running reactor build, and a second copy of that judgement here is how the panel and
/// the leaf come to disagree about which rules are valid. What this API owes is that it does not store
/// something that is not a rules file, and that it reports the leaf's verdict rather than its own.
/// </remarks>
public sealed class ReactorRulesTests
{
    [Fact]
    public void A_rules_document_is_recognised()
    {
        Assert.True(ReactorRulesService.IsRulesDocument("""{"rules":[]}""", out string? problem));
        Assert.Null(problem);
    }

    /// <summary>
    /// ⚠ An empty rule set is a rules file, and storing one is a real instruction.
    /// </summary>
    /// <remarks>
    /// "This host runs no rules" is something somebody can mean. Refusing it would leave the only way to
    /// express it being to delete the file, which reverts to the rules the leaf ships — the opposite of
    /// what was asked for.
    /// </remarks>
    [Fact]
    public void An_empty_rule_set_is_a_rules_file()
    {
        Assert.True(ReactorRulesService.IsRulesDocument("""{ "rules": [] }""", out _));
    }

    [Theory]
    [InlineData("[]", "object with a 'rules' array")]
    [InlineData("""{"notRules":[]}""", "needs a 'rules' array")]
    [InlineData("""{"rules":{}}""", "needs a 'rules' array")]
    public void Something_that_is_not_a_rules_file_is_refused_with_what_was_wrong(
        string body, string expected)
    {
        Assert.False(ReactorRulesService.IsRulesDocument(body, out string? problem));
        Assert.Contains(expected, problem);
    }

    /// <summary>
    /// ⚠ A parse failure names where, because that is the difference between a fixable typo and a file
    /// somebody rewrites from scratch.
    /// </summary>
    [Fact]
    public void Unparseable_rules_are_refused_with_a_position()
    {
        Assert.False(ReactorRulesService.IsRulesDocument("""{"rules":[,]}""", out string? problem));
        Assert.Contains("line", problem);
        Assert.Contains("position", problem);
    }

    // ---- what the leaf said afterwards ----

    [Fact]
    public void The_leafs_problems_and_live_rules_are_read_off_its_status()
    {
        const string status = """
            {
              "leaf": "kgsm-reactor",
              "problems": ["broken_one compares 'footprint.spanDaze', which this build does not measure"],
              "rules": [{ "id": "give_up_backup" }, { "id": "big_worlds" }]
            }
            """;

        Assert.True(ReactorRulesService.TryReadStatus(status, out var problems, out var live));

        Assert.Single(problems);
        Assert.Equal(["give_up_backup", "big_worlds"], live);
    }

    /// <summary>
    /// ⚠ A clean apply is an empty problem list, not an absent one.
    /// </summary>
    /// <remarks>
    /// The leaf reports the field either way, and a reader that treated absence as failure would report
    /// every good write as broken.
    /// </remarks>
    [Fact]
    public void A_status_with_nothing_wrong_reads_as_nothing_wrong()
    {
        Assert.True(ReactorRulesService.TryReadStatus(
            """{"problems":[],"rules":[{"id":"give_up_backup"}]}""", out var problems, out var live));

        Assert.Empty(problems);
        Assert.Equal(["give_up_backup"], live);
    }

    /// <summary>
    /// A status this build cannot read is not a status with nothing in it.
    /// </summary>
    /// <remarks>
    /// The caller polls until it gets a readable one and then says the leaf never reported back — never
    /// "no problems", which is the one answer that would let a broken write look like a good one.
    /// </remarks>
    [Fact]
    public void An_unreadable_status_is_not_mistaken_for_a_clean_one()
    {
        Assert.False(ReactorRulesService.TryReadStatus("not json", out _, out _));
    }

    [Fact]
    public void A_status_missing_the_fields_reports_neither_rather_than_guessing()
    {
        Assert.True(ReactorRulesService.TryReadStatus(
            """{"leaf":"kgsm-reactor"}""", out var problems, out var live));

        Assert.Empty(problems);
        Assert.Empty(live);
    }
}
