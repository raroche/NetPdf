// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using NetPdf.Text.Bidi;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 — the line builder. Owns the inline-content
/// pipeline that converts a sequence of styled <see cref="TextRun"/>s
/// into a sequence of positioned line fragments ready for the
/// painter (Phase 4). Per the Phase 3 plan §"InlineLayouter +
/// LineBuilder":
///
/// <list type="number">
///   <item><b>Bidi</b> (UAX #9 from <c>NetPdf.Text.Bidi</c>) →
///   resolved per-codepoint levels.</item>
///   <item><b>Itemization</b>: split into runs of (direction, script,
///   font, style) — no two adjacent codepoints in a single
///   <see cref="ItemizedRun"/> can have different shaping
///   prerequisites.</item>
///   <item><b>Shape</b> each run via
///   <c>NetPdf.Text.Shaping.HbShaper.Shape</c>.</item>
///   <item><b>Break opportunities</b> via UAX #14 line-break +
///   <c>hyphens: auto</c> consulting Liang patterns.</item>
///   <item><b>Measure</b> each shaped run's advance.</item>
///   <item><b>Wrap</b> lines, applying <c>text-align</c>,
///   <c>white-space</c>, <c>overflow-wrap</c>, <c>word-break</c>,
///   <c>vertical-align</c>.</item>
/// </list>
///
/// <para><b>Cycle 1 scope (this revision).</b> Steps 1+2 only —
/// bidi resolution + itemization-by-direction-and-style. Cycle 1
/// produces an <see cref="ItemizedRun"/> array; cycle 2 plugs in
/// the shaper; cycle 3 ships line breaking + wrapping; cycle 4
/// integrates with <c>InlineLayouter</c> (Task 10).</para>
///
/// <para><b>Itemization rules (cycle 1).</b> A run boundary is
/// created when EITHER the bidi level OR the source TextRun
/// changes between adjacent characters. Script-change boundaries
/// (UAX #24) are deferred to cycle 2 — most documents are single-
/// script (Latin) so cycle 1 produces correct output for them; mixed-
/// script documents will see fewer boundaries than a real engine
/// would create, which the cycle 2 shaper-integration will fix.</para>
///
/// <para><b>Threading.</b> <see cref="LineBuilder"/> is stateless;
/// every call is self-contained. No instance fields. Future cycles
/// may cache shaping results — that caching will live in a separate
/// shaper-pool type, not on this class, to keep <see cref="Itemize"/>
/// pure.</para>
/// </summary>
internal static class LineBuilder
{
    /// <summary>Per Phase 3 Task 9 cycle 1 — itemize a sequence of
    /// styled <see cref="TextRun"/>s into <see cref="ItemizedRun"/>s
    /// suitable for the shaper. Runs the UAX #9 bidi algorithm on the
    /// concatenated text + emits run boundaries at every level change
    /// + every source-TextRun change.
    ///
    /// <para>Per CSS Writing Modes L3 — <paramref name="paragraphDirection"/>
    /// maps to the CSS <c>direction</c> property at the inline-pass's
    /// containing block: <c>ltr</c> = <see cref="ParagraphDirection.LeftToRight"/>,
    /// <c>rtl</c> = <see cref="ParagraphDirection.RightToLeft"/>.
    /// CSS doesn't expose the bidi <c>auto</c> mode at the
    /// <c>direction</c> level (it requires <c>unicode-bidi: plaintext</c>
    /// in CSS Writing Modes L3); cycle 1 callers should pass
    /// <see cref="ParagraphDirection.LeftToRight"/> for typical
    /// LTR documents.</para>
    ///
    /// <para>Returns an empty array when <paramref name="textRuns"/>
    /// is empty or every text run is empty. Mandatory line-break
    /// codepoints (LF, CR, NEL, ¶, etc.) are PRESERVED in the run
    /// (they don't create boundaries themselves; cycle 3's wrapper
    /// reads them as `Mandatory` line-break opportunities to force a
    /// line break).</para></summary>
    /// <param name="textRuns">The source text runs in document order.
    /// Empty runs are silently skipped (don't create itemized runs).</param>
    /// <param name="paragraphDirection">The paragraph-level base
    /// direction (UAX #9 P2/P3).</param>
    /// <returns>Itemized runs covering the full text in document
    /// order. Each run's <see cref="ItemizedRun.Utf16Start"/> +
    /// <see cref="ItemizedRun.Utf16Length"/> indexes into the
    /// concatenated input text (= the source TextRuns joined).</returns>
    public static ItemizedRun[] Itemize(
        IReadOnlyList<TextRun> textRuns,
        ParagraphDirection paragraphDirection)
    {
        ArgumentNullException.ThrowIfNull(textRuns);
        if (textRuns.Count == 0)
        {
            return Array.Empty<ItemizedRun>();
        }

        // Concatenate all text runs into a single buffer + build a
        // char-index → source-run-index map. The bidi algorithm
        // works on a single contiguous text; we'll split the levels
        // back out to per-run extents at the end.
        var totalLength = 0;
        for (var i = 0; i < textRuns.Count; i++)
        {
            totalLength += textRuns[i].Text.Length;
        }
        if (totalLength == 0)
        {
            return Array.Empty<ItemizedRun>();
        }

        var concatBuf = new char[totalLength];
        var sourceRunIndices = new int[totalLength];
        var pos = 0;
        for (var runIdx = 0; runIdx < textRuns.Count; runIdx++)
        {
            var run = textRuns[runIdx];
            for (var c = 0; c < run.Text.Length; c++)
            {
                concatBuf[pos] = run.Text[c];
                sourceRunIndices[pos] = runIdx;
                pos++;
            }
        }

        // Resolve bidi levels per-codepoint. The pipeline takes a
        // Span<BidiCharInfo> + a paragraph level (byte) — the
        // ParagraphLevelResolver maps the requested direction (incl.
        // Auto P2/P3) to a level.
        var paragraphLevel = ParagraphLevelResolver.Resolve(
            concatBuf, paragraphDirection);
        var charInfos = BidiPipeline.BuildCharInfos(concatBuf);
        var bidiLevels = BidiPipeline.ResolveLevelsForUtf16(
            charInfos, paragraphLevel, totalLength);

        // Walk the concatenated text + emit a new ItemizedRun
        // whenever the bidi level OR the source-run-index changes.
        var output = new List<ItemizedRun>(capacity: textRuns.Count);
        var runStart = 0;
        var runLevel = bidiLevels[0];
        var runSourceIdx = sourceRunIndices[0];
        for (var i = 1; i < totalLength; i++)
        {
            var level = bidiLevels[i];
            var srcIdx = sourceRunIndices[i];
            if (level != runLevel || srcIdx != runSourceIdx)
            {
                output.Add(new ItemizedRun(
                    Utf16Start: runStart,
                    Utf16Length: i - runStart,
                    BidiLevel: runLevel,
                    SourceTextRunIndex: runSourceIdx));
                runStart = i;
                runLevel = level;
                runSourceIdx = srcIdx;
            }
        }
        // Tail run.
        output.Add(new ItemizedRun(
            Utf16Start: runStart,
            Utf16Length: totalLength - runStart,
            BidiLevel: runLevel,
            SourceTextRunIndex: runSourceIdx));

        return output.ToArray();
    }
}
