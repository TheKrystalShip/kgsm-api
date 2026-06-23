using System.Text.RegularExpressions;

namespace TheKrystalShip.Api.Services.Library;

/// <summary>
/// Cleans a RAWG <c>description_raw</c> into a short blurb for the catalog (the M8·a library increment, the
/// middle link of the description precedence chain: curated blueprint description → this cleaned RAWG text →
/// null). RAWG's raw text is plain but long and often carries <c>###</c>-headed sections (Story, Gameplay,
/// …); we take the lead paragraph(s), drop any markdown-ish section headers, collapse whitespace, and
/// truncate at a sentence boundary near ~500 chars.
/// </summary>
/// <remarks>
/// Pure + deterministic → unit-tested standalone (the worker stores the already-cleaned result so the
/// aggregator/serve path stays free of text munging). Honest: we never invent text — an empty/whitespace
/// input returns null, and we only ever trim, never paraphrase.
/// </remarks>
public static partial class RawgDescription
{
    /// <summary>Target length. We truncate at the last sentence boundary at/under <see cref="MaxLength"/>;
    /// if there is no sentence boundary in range we hard-cut at <see cref="MaxLength"/> on a word boundary
    /// and append an ellipsis. ~400–600 chars per the plan; 500 is the centre.</summary>
    internal const int MaxLength = 500;

    /// <summary>Below this we won't sentence-truncate (we'd lose too much) — a hard word-boundary cut + "…".</summary>
    internal const int MinSentenceCut = 200;

    [GeneratedRegex(@"^\s*#{1,6}\s*.*$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeaderLine();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex BlankLineRun();

    /// <summary>Clean the RAWG raw description, or null when there is nothing usable.</summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string text = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Replace("\r", "\n", StringComparison.Ordinal);

        // Strip any markdown-ish section headers (### Story, etc.) — keep the prose, drop the scaffolding.
        text = MarkdownHeaderLine().Replace(text, "");

        // Take the lead paragraph: everything up to the first blank-line run (RAWG separates sections with
        // a blank line). If the whole thing is one block, that's the lead paragraph.
        string lead = BlankLineRun().Split(text, 2)[0];

        // Collapse internal whitespace/newlines to single spaces, trim.
        lead = HorizontalWhitespace().Replace(lead.Replace('\n', ' '), " ").Trim();
        if (lead.Length == 0)
        {
            // The lead paragraph was only a header; fall back to the full collapsed text.
            lead = HorizontalWhitespace().Replace(text.Replace('\n', ' '), " ").Trim();
            if (lead.Length == 0) return null;
        }

        return Truncate(lead);
    }

    // Truncate to ~MaxLength at a sentence boundary; fall back to a word-boundary hard cut + ellipsis.
    private static string Truncate(string s)
    {
        if (s.Length <= MaxLength) return s;

        string window = s[..MaxLength];

        // Prefer a sentence boundary (. ! ?) — the last one comfortably into the window.
        int sentenceEnd = window.LastIndexOfAny(['.', '!', '?']);
        if (sentenceEnd >= MinSentenceCut)
            return window[..(sentenceEnd + 1)].TrimEnd();

        // No good sentence boundary — cut on the last whole word and ellipsize.
        int lastSpace = window.LastIndexOf(' ');
        string cut = lastSpace > MinSentenceCut ? window[..lastSpace] : window;
        return cut.TrimEnd() + "…";
    }
}
