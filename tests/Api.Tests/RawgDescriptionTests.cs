using TheKrystalShip.Api.Services.Library;

namespace TheKrystalShip.Api.Tests;

/// <summary>
/// The RAWG <c>description_raw</c> cleaner (the M8·a library increment). Pure + deterministic — proven
/// standalone (the worker stores the already-cleaned result). The honesty rule: only ever trim, never invent.
/// </summary>
public sealed class RawgDescriptionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n  \n")]
    public void Empty_input_returns_null(string? raw) => Assert.Null(RawgDescription.Clean(raw));

    [Fact]
    public void Short_clean_text_passes_through_unchanged()
    {
        const string s = "Factorio is a game about building and creating automated factories.";
        Assert.Equal(s, RawgDescription.Clean(s));
    }

    [Fact]
    public void Takes_the_lead_paragraph_and_drops_later_sections()
    {
        string raw = "The lead paragraph describes the game succinctly.\n\n"
                   + "A second paragraph with extra lore that should be dropped.";
        Assert.Equal("The lead paragraph describes the game succinctly.", RawgDescription.Clean(raw));
    }

    [Fact]
    public void Strips_markdown_section_headers()
    {
        string raw = "### Story\nA grand adventure begins in a distant land.";
        string? cleaned = RawgDescription.Clean(raw);
        Assert.NotNull(cleaned);
        Assert.DoesNotContain("###", cleaned);
        Assert.DoesNotContain("Story", cleaned);
        Assert.StartsWith("A grand adventure", cleaned);
    }

    [Fact]
    public void Collapses_internal_whitespace_and_newlines()
    {
        string raw = "Line one of the\nblurb   with    spaces\nand a wrap.";
        Assert.Equal("Line one of the blurb with spaces and a wrap.", RawgDescription.Clean(raw));
    }

    [Fact]
    public void Long_text_truncates_at_a_sentence_boundary_within_the_budget()
    {
        // Many short sentences → the cut lands on a '.' boundary at/under the max length, no ellipsis.
        string sentence = "This is one sentence about the game. ";
        string raw = string.Concat(Enumerable.Repeat(sentence, 40)); // ~1480 chars
        string? cleaned = RawgDescription.Clean(raw);

        Assert.NotNull(cleaned);
        Assert.True(cleaned!.Length <= RawgDescription.MaxLength, $"length {cleaned.Length} > {RawgDescription.MaxLength}");
        Assert.EndsWith(".", cleaned);              // sentence boundary, not a hard cut
        Assert.DoesNotContain("…", cleaned);
    }

    [Fact]
    public void A_single_giant_sentence_hard_cuts_on_a_word_boundary_with_an_ellipsis()
    {
        // One sentence longer than the budget with no early boundary → word-boundary cut + ellipsis.
        string raw = string.Concat(Enumerable.Repeat("word ", 200)).Trim(); // 1000 chars, no '.'
        string? cleaned = RawgDescription.Clean(raw);

        Assert.NotNull(cleaned);
        Assert.True(cleaned!.Length <= RawgDescription.MaxLength + 1); // +1 for the ellipsis char
        Assert.EndsWith("…", cleaned);
        Assert.DoesNotContain("wor…", cleaned);     // cut on a whole word, never mid-word
    }
}
