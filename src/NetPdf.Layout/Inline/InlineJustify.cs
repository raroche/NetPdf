// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Shared <c>text-align: justify</c> geometry (CSS Text 3 §7.3) — the SINGLE source of truth for a
/// line's inter-word justification opportunities, used by BOTH <c>TextPainter</c> (which spreads the
/// line's free space across the gaps + splits the glyph run at them) and the layout's inline-atomic
/// placement (which shifts an atomic RIGHT by the gaps accumulated before it). Keeping the gap-count +
/// the word-separator-space test on one helper means the line the painter justifies and the offset the
/// layout gives an atomic on that line can never disagree.
/// </summary>
internal static class InlineJustify
{
    /// <summary>Whether the cluster at <paramref name="cluster"/> in <paramref name="concatText"/> is a
    /// word-separator space — an ASCII space (U+0020) or tab (U+0009), the justification opportunities
    /// (CSS Text 3 §7.3; a NO-BREAK space U+00A0 is deliberately NOT one). Bounds-guarded (an
    /// out-of-range cluster is not a space — safe degradation), matching the line builder.</summary>
    public static bool IsJustifySpace(string concatText, int cluster) =>
        (uint)cluster < (uint)concatText.Length
        && (concatText[cluster] == ' ' || concatText[cluster] == '\t');

    /// <summary>Count a line's INTERIOR word-separator spaces — the justification opportunities. A
    /// TRAILING run of spaces is excluded (they sort last and get no gap — a word OR an inline atomic
    /// must follow). An inline ATOMIC contributes advance but is not itself an opportunity; because it
    /// is word-like content it ENDS a trailing-space run (a space immediately before an atomic is
    /// interior). A glyph's <c>Cluster</c> indexes into <paramref name="concatText"/> (the shaping
    /// concatenation), matching the line builder's convention.</summary>
    public static int InteriorGapCount(
        LineFragment line, IReadOnlyList<ShapedRun> shapedRuns, string concatText)
    {
        var totalSpaces = 0;
        var trailingSpaces = 0;
        foreach (var slice in line.Slices)
        {
            if (slice.GlyphLength <= 0) continue;
            var run = shapedRuns[slice.ShapedRunIndex];
            if (run.Atomic is not null)
            {
                trailingSpaces = 0;   // word-like content → a space immediately before it is interior.
                continue;
            }
            for (var g = 0; g < slice.GlyphLength; g++)
            {
                if (IsJustifySpace(concatText, run.Glyphs[slice.GlyphStart + g].Cluster))
                {
                    totalSpaces++;
                    trailingSpaces++;
                }
                else
                {
                    trailingSpaces = 0;
                }
            }
        }
        return totalSpaces - trailingSpaces;   // interior (word- or atomic-followed) spaces only.
    }
}
