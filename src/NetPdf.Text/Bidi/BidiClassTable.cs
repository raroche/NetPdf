// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Maps Unicode codepoints to their <see cref="BidiClass"/> per UCD
/// <c>DerivedBidiClass.txt</c>. Stage 12.2 delegates to a sorted-range lookup
/// (<see cref="BidiClassUcdRanges.Lookup"/>) hand-curated against UCD knowledge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 12.2 status:</b> the underlying range table covers ASCII, Latin-1, all major
/// LTR scripts (Greek, Cyrillic, Armenian, Latin Extended, IPA, Indic, Thai, CJK,
/// Hiragana, Katakana, Hangul, Yi, etc.), all RTL scripts (Hebrew, Arabic + extensions
/// + presentation forms, Syriac, Thaana, NKo, Samaritan, Mandaic, Hanifi Rohingya,
/// ancient RTL scripts), the Arabic Mathematical Alphabetic Symbols, plus the bidi-
/// relevant supplementary blocks (emoji as ON, Tag characters as BN, Variation
/// Selectors as NSM). Coverage for combining-mark NSM bits inside Indic and Tibetan
/// blocks is intentionally coarse (those ranges are L by default in the table) — the
/// Roslyn source generator that lands in Stage 12.2.x will refine per-codepoint
/// from the actual UCD file.
/// </para>
/// <para>
/// <b>Default fallback:</b> codepoints outside any explicit range default to
/// <see cref="BidiClass.L"/>, matching UCD's "default L" rule for unassigned ranges.
/// </para>
/// </remarks>
internal static class BidiClassTable
{
    /// <summary>Look up the bidi class of a Unicode codepoint.</summary>
    public static BidiClass GetClass(int codepoint)
    {
        if ((uint)codepoint > 0x10FFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(codepoint), codepoint,
                "Codepoint must be in the Unicode range [0, 0x10FFFF].");
        }
        return BidiClassUcdRanges.Lookup(codepoint);
    }
}
