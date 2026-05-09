// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NetPdf.Text.Bidi;
using NetPdf.Text.LineBreaking;
using NetPdf.Text.Shaping;

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
/// <para><b>Itemization rules (cycle 1+2).</b> A run boundary is
/// created when EITHER the bidi level OR the source TextRun
/// changes between adjacent characters. Script-change boundaries
/// (UAX #24) are deferred to <b>cycle 3</b> — re-scoped from the
/// original cycle-2 plan; cycle 2 wired up the shaper but kept
/// itemization at cycle-1 granularity. Per-run UAX #24 detection
/// + script tagging will land alongside the wrapping pass.
/// Until then mixed-script documents will see fewer boundaries
/// than a real engine would create — callers MUST pass an
/// explicit <c>scriptIso15924</c> + <c>language</c> to
/// <see cref="Shape"/> to avoid silent Latin-bias mis-shaping of
/// non-Latin runs.</para>
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

        // Resolve bidi levels per-codepoint via the high-level
        // BidiAlgorithm.ResolveLevels API. Per cycle 1 post-PR-32
        // review (Copilot #1) — pre-fix called BidiPipeline directly
        // with a single paragraph-level for the entire concatenated
        // buffer, but UAX #9 §3.3.1 P1 requires per-paragraph
        // resolution. ParagraphLevelResolver.Resolve's Auto scan
        // also stops at the first B/paragraph separator, so
        // multi-paragraph Auto input would have used the FIRST
        // paragraph's level for ALL paragraphs — a real bug for
        // mixed-direction multi-paragraph documents.
        //
        // BidiAlgorithm.ResolveLevels splits the input on UCD class-B
        // characters (LF, CR, NEL, PARAGRAPH SEPARATOR, etc.) +
        // resolves each paragraph independently with its own P2/P3-
        // resolved level. Concatenated output is byte-deterministic.
        var bidiLevels = BidiAlgorithm.ResolveLevels(
            concatBuf, paragraphDirection);

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

    /// <summary>Per Phase 3 Task 9 cycle 2 — shaping pass. Takes
    /// the <see cref="ItemizedRun"/>s produced by <see cref="Itemize"/>
    /// + the original source <see cref="TextRun"/>s + a
    /// <see cref="IShaperResolver"/> + per-run shaping metadata
    /// (script + language); returns a <see cref="ShapedRun"/> for
    /// each itemized run.
    ///
    /// <para>For each itemized run the method:</para>
    /// <list type="number">
    ///   <item>Validates <see cref="ItemizedRun.SourceTextRunIndex"/>
    ///   is a valid index into <paramref name="sourceTextRuns"/> +
    ///   <see cref="ItemizedRun.Utf16Start"/>+<see cref="ItemizedRun.Utf16Length"/>
    ///   stay inside the concatenated source text. Mismatched lists
    ///   (e.g. itemized runs from a different source) throw
    ///   <see cref="ArgumentException"/> with a clear message — fail
    ///   early instead of <see cref="IndexOutOfRangeException"/>.</item>
    ///   <item>Resolves a <see cref="HbShaper"/> via
    ///   <paramref name="resolver"/> using the source TextRun's
    ///   computed style.</item>
    ///   <item>Determines shaping direction from
    ///   <see cref="ItemizedRun.IsRtl"/>.</item>
    ///   <item>Calls the full-buffer
    ///   <see cref="HbShaper.Shape(System.ReadOnlySpan{char},int,int,ShapingDirection,string,string,System.Threading.CancellationToken)"/>
    ///   overload — the FULL concat text is passed as the buffer +
    ///   <c>itemOffset</c>=<see cref="ItemizedRun.Utf16Start"/> +
    ///   <c>itemLength</c>=<see cref="ItemizedRun.Utf16Length"/>. This
    ///   keeps glyph cluster indices concat-buffer relative + lets
    ///   HarfBuzz use surrounding context for shaping decisions
    ///   (Arabic joining across <c>TextRun</c> boundaries, complex
    ///   reordering, etc.) per <c>hb_buffer_add_utf16</c>'s contract.</item>
    ///   <item>Sums <see cref="ShapedGlyph.XAdvance"/> across the
    ///   returned glyphs into <see cref="ShapedRun.TotalAdvance"/>
    ///   for cycle 3's wrap pass.</item>
    /// </list>
    ///
    /// <para><b>Cycle 2 review fix — explicit script/language only.</b>
    /// <paramref name="scriptIso15924"/> + <paramref name="language"/>
    /// are required (no defaults). The earlier cycle-2 ship had
    /// defaults of <c>"Latn"</c> / <c>"en"</c> which silently
    /// mis-shaped Arabic / Hebrew / Indic / CJK / Thai runs as Latin.
    /// Until per-run UAX #24 script detection lands (cycle 3),
    /// callers MUST pass explicit metadata so the failure mode is
    /// "compile error" (missing arg), not "wrong glyphs ship to a
    /// PDF and look plausible to a Latin-only reviewer".</para>
    ///
    /// <para><b>Cycle 2 simplifications.</b></para>
    /// <list type="bullet">
    ///   <item><paramref name="scriptIso15924"/> +
    ///   <paramref name="language"/> are passed through to every run
    ///   uniformly. Cycle 3 will add UAX #24 script detection +
    ///   per-run script tagging at itemization time so each run gets
    ///   its appropriate script + the correct OpenType feature
    ///   selection.</item>
    ///   <item>The concat text is rebuilt internally from the source
    ///   <see cref="TextRun"/>s — the cost is O(N) where N is the
    ///   total UTF-16 length, comparable to one extra string copy.
    ///   Cycle 3 may pool a buffer.</item>
    /// </list></summary>
    /// <param name="sourceTextRuns">The original source runs passed
    /// to <see cref="Itemize"/>. Used to (a) rebuild the concat text
    /// + (b) read each itemized run's source style for
    /// <paramref name="resolver"/>.</param>
    /// <param name="itemizedRuns">The output of <see cref="Itemize"/>
    /// for the same source runs. Each run's
    /// <see cref="ItemizedRun.SourceTextRunIndex"/> must be a valid
    /// index into <paramref name="sourceTextRuns"/> +
    /// <see cref="ItemizedRun.Utf16Start"/>+<see cref="ItemizedRun.Utf16Length"/>
    /// must fit inside the concatenated source text — mismatched
    /// lists throw <see cref="ArgumentException"/>.</param>
    /// <param name="resolver">Resolves a <see cref="HbShaper"/> per
    /// style. The resolver owns the returned shapers — they are not
    /// disposed by this method.</param>
    /// <param name="scriptIso15924">ISO 15924 script tag (4 letters)
    /// passed to every shaping call. Required (no default — see XML
    /// summary). Cycle 3 will derive per-run from UAX #24.</param>
    /// <param name="language">BCP 47 language tag passed to every
    /// shaping call. Required.</param>
    /// <param name="cancellationToken">Checked between runs. The
    /// per-run native HarfBuzz call is microseconds; cancellation is
    /// most useful for documents with thousands of itemized runs.</param>
    /// <exception cref="ArgumentException">
    /// <para>An <see cref="ItemizedRun.SourceTextRunIndex"/> is out
    /// of range for <paramref name="sourceTextRuns"/>.</para>
    /// <para>OR <see cref="ItemizedRun.Utf16Start"/> is negative or
    /// <see cref="ItemizedRun.Utf16Start"/>+<see cref="ItemizedRun.Utf16Length"/>
    /// extends past the concatenated source text length.</para>
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.
    /// </exception>
    public static ShapedRun[] Shape(
        IReadOnlyList<TextRun> sourceTextRuns,
        IReadOnlyList<ItemizedRun> itemizedRuns,
        IShaperResolver resolver,
        string scriptIso15924,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceTextRuns);
        ArgumentNullException.ThrowIfNull(itemizedRuns);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scriptIso15924);
        ArgumentNullException.ThrowIfNull(language);

        if (itemizedRuns.Count == 0)
        {
            return Array.Empty<ShapedRun>();
        }

        // Rebuild the concat text. Cheap O(N); avoids needing
        // Itemize to plumb it back to the caller (which would break
        // the existing Itemize signature + tests).
        var concatTotal = 0;
        for (var i = 0; i < sourceTextRuns.Count; i++)
        {
            concatTotal += sourceTextRuns[i].Text.Length;
        }
        var concatBuf = new StringBuilder(concatTotal);
        for (var i = 0; i < sourceTextRuns.Count; i++)
        {
            concatBuf.Append(sourceTextRuns[i].Text);
        }
        var concatText = concatBuf.ToString();

        var output = new ShapedRun[itemizedRuns.Count];
        for (var runIdx = 0; runIdx < itemizedRuns.Count; runIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var run = itemizedRuns[runIdx];

            // Coherence checks — fail early with a descriptive message
            // when itemizedRuns came from a different source list,
            // instead of throwing IndexOutOfRangeException deep in the
            // span slice / array index.
            if ((uint)run.SourceTextRunIndex >= (uint)sourceTextRuns.Count)
            {
                throw new ArgumentException(
                    $"LineBuilder.Shape: itemizedRuns[{runIdx}].SourceTextRunIndex={run.SourceTextRunIndex} is out of range for sourceTextRuns (count={sourceTextRuns.Count}). Did the itemized runs come from a different source list?",
                    nameof(itemizedRuns));
            }
            if (run.Utf16Start < 0 || run.Utf16Length < 0 ||
                (long)run.Utf16Start + run.Utf16Length > concatTotal)
            {
                throw new ArgumentException(
                    $"LineBuilder.Shape: itemizedRuns[{runIdx}] range [Utf16Start={run.Utf16Start}, Utf16Length={run.Utf16Length}] is out of bounds for the concatenated source text (length={concatTotal}).",
                    nameof(itemizedRuns));
            }

            var style = sourceTextRuns[run.SourceTextRunIndex].Style;
            var direction = run.IsRtl
                ? ShapingDirection.RightToLeft
                : ShapingDirection.LeftToRight;

            // Pass the FULL concat buffer + (Utf16Start, Utf16Length)
            // so HarfBuzz sees left/right context for cross-source-
            // boundary contextual shaping (Arabic joining across
            // TextRuns, complex-script reordering, etc.) + cluster
            // indices stay concat-buffer relative.
            var shaper = resolver.Resolve(style);
            var glyphs = shaper.Shape(
                concatText.AsSpan(),
                run.Utf16Start, run.Utf16Length,
                direction, scriptIso15924, language,
                cancellationToken);

            // Cycle 2 — sum XAdvance for fast wrap-pass measurement.
            // HarfBuzz XAdvance is in CSS px (HbShaper handles font-
            // units → pixels conversion at construction time).
            double totalAdvance = 0;
            for (var g = 0; g < glyphs.Length; g++)
            {
                totalAdvance += glyphs[g].XAdvance;
            }

            output[runIdx] = new ShapedRun(run, glyphs, totalAdvance);
        }
        return output;
    }

    /// <summary>Per Phase 3 Task 9 cycle 3a — wrapping pass. Takes
    /// the <see cref="ShapedRun"/>s produced by <see cref="Shape"/>
    /// + the original source <see cref="TextRun"/>s + the available
    /// inline-axis size; emits a <see cref="LineFragment"/> array
    /// representing one wrapped line per fragment.
    ///
    /// <para><b>Algorithm (cycle 3a — naive greedy).</b></para>
    /// <list type="number">
    ///   <item>Rebuild the concatenated source text from
    ///   <paramref name="sourceTextRuns"/> + run UAX #14
    ///   (<see cref="LineBreakAlgorithm.FindBreaks"/>) on it.</item>
    ///   <item>Flatten all glyphs across all shaped runs into a
    ///   single linear array <c>(runIdx, glyphIdx, glyph,
    ///   breakOpportunity)</c>. Indexing into this flat array as
    ///   "global glyph index" simplifies the line-fill loop.</item>
    ///   <item>Walk the flat array with two cursors:
    ///   <c>lineStart</c> + <c>cursor</c>. Track the most recent
    ///   <c>Allowed</c> break opportunity in
    ///   <c>[lineStart, cursor]</c> as a candidate snap-back point.</item>
    ///   <item>If accumulated advance exceeds
    ///   <paramref name="availableInlineSize"/>: snap back to the
    ///   candidate, slice <c>[lineStart, candidate]</c> into per-run
    ///   <see cref="ShapedRunSlice"/>s, emit a
    ///   <see cref="LineFragment"/>, set <c>lineStart = candidate+1
    ///   = cursor</c>, retry. If no candidate exists, emit at the
    ///   current position (cycle 3a allows overflow; cycle 3b adds
    ///   <c>overflow-wrap</c>/<c>word-break</c>).</item>
    ///   <item><c>Mandatory</c> breaks force a fragment boundary
    ///   regardless of advance — paragraph separators, LF, CR, NEL,
    ///   etc.</item>
    /// </list>
    ///
    /// <para><b>Cycle 3a deferrals.</b> See <see cref="LineFragment"/>
    /// XML doc for the full list — white-space variants, hyphenation,
    /// overflow-wrap, word-break, text-align, vertical-align, RTL
    /// fragment-level reversal — all in cycle 3b/c.</para></summary>
    /// <param name="sourceTextRuns">The original source runs passed
    /// to <see cref="Itemize"/> + <see cref="Shape"/>. Used to rebuild
    /// the concatenated text for UAX #14 line-break analysis.</param>
    /// <param name="shapedRuns">The output of <see cref="Shape"/> for
    /// the same source runs.</param>
    /// <param name="availableInlineSize">The maximum inline-axis size
    /// of a line, in CSS px. Glyphs whose cumulative advance exceeds
    /// this value are wrapped to a new line at the most recent UAX #14
    /// <c>Allowed</c> break. Must be positive + finite.</param>
    /// <param name="cancellationToken">Checked once per emitted line —
    /// most useful for documents with thousands of wrapped lines.</param>
    /// <returns>One <see cref="LineFragment"/> per wrapped line in
    /// document order. Empty array when <paramref name="shapedRuns"/>
    /// is empty.</returns>
    /// <exception cref="ArgumentNullException">A required argument is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="availableInlineSize"/> is non-positive or
    /// non-finite.</exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.</exception>
    public static LineFragment[] Wrap(
        IReadOnlyList<TextRun> sourceTextRuns,
        IReadOnlyList<ShapedRun> shapedRuns,
        double availableInlineSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceTextRuns);
        ArgumentNullException.ThrowIfNull(shapedRuns);
        if (!double.IsFinite(availableInlineSize) || availableInlineSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableInlineSize),
                availableInlineSize,
                "LineBuilder.Wrap: availableInlineSize must be a positive finite value (CSS px).");
        }

        if (shapedRuns.Count == 0)
        {
            return Array.Empty<LineFragment>();
        }

        // Rebuild concat text for UAX #14 line-break analysis.
        var concatTotal = 0;
        for (var i = 0; i < sourceTextRuns.Count; i++)
        {
            concatTotal += sourceTextRuns[i].Text.Length;
        }
        var concatBuf = new StringBuilder(concatTotal);
        for (var i = 0; i < sourceTextRuns.Count; i++)
        {
            concatBuf.Append(sourceTextRuns[i].Text);
        }
        var concatText = concatBuf.ToString();

        // breaks[i] = opportunity AFTER UTF-16 code unit i. Final
        // entry is always Mandatory per LB3.
        var breaks = LineBreakAlgorithm.FindBreaks(concatText.AsSpan());

        // Flatten glyphs across all runs. flat[i] = (runIdx,
        // glyphIdxInRun, advance, opp). Compute total once for
        // capacity allocation.
        var totalGlyphs = 0;
        for (var i = 0; i < shapedRuns.Count; i++)
        {
            totalGlyphs += shapedRuns[i].Glyphs.Length;
        }
        if (totalGlyphs == 0)
        {
            return Array.Empty<LineFragment>();
        }

        var flat = new FlatGlyph[totalGlyphs];
        var flatIdx = 0;
        for (var r = 0; r < shapedRuns.Count; r++)
        {
            var glyphs = shapedRuns[r].Glyphs;
            for (var g = 0; g < glyphs.Length; g++)
            {
                var glyph = glyphs[g];
                var cluster = glyph.Cluster;
                var opp = LineBreakOpportunity.Prohibited;
                if (cluster >= 0 && cluster < breaks.Length)
                {
                    opp = breaks[cluster];
                }
                flat[flatIdx++] = new FlatGlyph(
                    RunIdx: r,
                    GlyphIdxInRun: g,
                    Advance: glyph.XAdvance,
                    Opportunity: opp);
            }
        }

        var output = new List<LineFragment>(capacity: 4);
        var lineStart = 0;
        var lineAdvance = 0.0;
        var lastAllowed = -1; // global glyph index of the last Allowed opportunity

        for (var cursor = 0; cursor < totalGlyphs; cursor++)
        {
            var item = flat[cursor];
            var afterAdvance = lineAdvance + item.Advance;

            if (afterAdvance > availableInlineSize && lastAllowed >= 0
                && lastAllowed >= lineStart)
            {
                // Soft-wrap: snap back to lastAllowed.
                EmitFragmentRange(output, shapedRuns, flat, lineStart, lastAllowed,
                    endsWithMandatoryBreak: false);
                lineStart = lastAllowed + 1;
                cursor = lineStart - 1; // for-loop increment lands on lineStart
                lineAdvance = 0;
                lastAllowed = -1;
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            lineAdvance = afterAdvance;

            if (item.Opportunity == LineBreakOpportunity.Mandatory)
            {
                EmitFragmentRange(output, shapedRuns, flat, lineStart, cursor,
                    endsWithMandatoryBreak: true);
                lineStart = cursor + 1;
                lineAdvance = 0;
                lastAllowed = -1;
                cancellationToken.ThrowIfCancellationRequested();
            }
            else if (item.Opportunity == LineBreakOpportunity.Allowed)
            {
                lastAllowed = cursor;
            }
        }

        // Tail — any glyphs from lineStart..totalGlyphs-1 that didn't
        // hit a Mandatory or overflow snap-back. Per LB3 the last
        // codepoint's opportunity is always Mandatory, so this only
        // fires if the last glyph's cluster didn't map to the
        // mandatory entry (e.g., font with ligatures collapsing the
        // last codepoint into an earlier cluster).
        if (lineStart < totalGlyphs)
        {
            EmitFragmentRange(output, shapedRuns, flat, lineStart, totalGlyphs - 1,
                endsWithMandatoryBreak: false);
        }

        return output.ToArray();
    }

    /// <summary>Slice a global glyph range <c>[start, end]</c>
    /// (inclusive on both ends) into per-run
    /// <see cref="ShapedRunSlice"/>s + emit a
    /// <see cref="LineFragment"/>.</summary>
    private static void EmitFragmentRange(
        List<LineFragment> output,
        IReadOnlyList<ShapedRun> shapedRuns,
        FlatGlyph[] flat, int start, int end,
        bool endsWithMandatoryBreak)
    {
        if (start > end)
        {
            // Empty line (e.g., back-to-back Mandatory).
            output.Add(new LineFragment(
                Slices: Array.Empty<ShapedRunSlice>(),
                TotalAdvance: 0,
                EndsWithMandatoryBreak: endsWithMandatoryBreak));
            return;
        }

        var slices = new List<ShapedRunSlice>(capacity: 1);
        var totalAdvance = 0.0;

        var sliceRunIdx = flat[start].RunIdx;
        var sliceStartGlyph = flat[start].GlyphIdxInRun;
        var sliceAdvance = 0.0;
        var sliceCount = 0;

        for (var i = start; i <= end; i++)
        {
            var item = flat[i];
            if (item.RunIdx != sliceRunIdx)
            {
                // Close the current slice; start a new one.
                slices.Add(new ShapedRunSlice(
                    ShapedRunIndex: sliceRunIdx,
                    GlyphStart: sliceStartGlyph,
                    GlyphLength: sliceCount,
                    SliceAdvance: sliceAdvance));
                totalAdvance += sliceAdvance;
                sliceRunIdx = item.RunIdx;
                sliceStartGlyph = item.GlyphIdxInRun;
                sliceAdvance = 0;
                sliceCount = 0;
            }
            sliceAdvance += item.Advance;
            sliceCount++;
        }

        // Final slice.
        slices.Add(new ShapedRunSlice(
            ShapedRunIndex: sliceRunIdx,
            GlyphStart: sliceStartGlyph,
            GlyphLength: sliceCount,
            SliceAdvance: sliceAdvance));
        totalAdvance += sliceAdvance;

        output.Add(new LineFragment(
            Slices: slices.ToArray(),
            TotalAdvance: totalAdvance,
            EndsWithMandatoryBreak: endsWithMandatoryBreak));
    }

    /// <summary>Cycle 3a internal: a flattened glyph view across all
    /// shaped runs, indexed by global glyph position.</summary>
    private readonly record struct FlatGlyph(
        int RunIdx,
        int GlyphIdxInRun,
        float Advance,
        LineBreakOpportunity Opportunity);
}
