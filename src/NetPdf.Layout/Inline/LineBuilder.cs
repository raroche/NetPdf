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
    ///   <item>Validate args + check coherence: each shaped run's
    ///   <see cref="ShapedRun.Source"/> indexes a valid source run +
    ///   the run's UTF-16 range fits inside the concatenated text +
    ///   each glyph's <see cref="ShapedGlyph.Cluster"/> is in range +
    ///   each glyph's <see cref="ShapedGlyph.XAdvance"/> is finite +
    ///   non-negative. Mismatched lists throw
    ///   <see cref="ArgumentException"/> with descriptive messages.</item>
    ///   <item>Rebuild the concatenated source text from
    ///   <paramref name="sourceTextRuns"/> + run UAX #14
    ///   (<see cref="LineBreakAlgorithm.FindBreaks"/>) on it.
    ///   <c>breaks[i]</c> is the opportunity AFTER UTF-16 code unit
    ///   <c>i</c>.</item>
    ///   <item>Flatten all glyphs across all shaped runs into a
    ///   linear array. For each glyph, compute the cluster-end (one-
    ///   past-end UTF-16 offset) and look up
    ///   <c>breaks[clusterEnd-1]</c> — i.e., the break-opportunity
    ///   AFTER the cluster, NOT after the cluster's first code unit.
    ///   This handles surrogate-pair codepoints (emoji), combining
    ///   marks, and ligatures correctly. For LTR runs, cluster-end
    ///   is the next glyph's cluster (or the run's UTF-16 end);
    ///   cycle 3a uses <c>cluster+1</c> as a fallback for RTL +
    ///   pins RTL multi-codeunit accuracy as a cycle 3c
    ///   improvement.</item>
    ///   <item>Tag each glyph that's a UAX #14 hard-line-break
    ///   control (LF, CR, VT, FF, NEL, LS, PS) so the wrap loop can
    ///   exclude it from the drawable slice — the painter must NOT
    ///   emit glyph data for control characters.</item>
    ///   <item>Walk the flat array with two cursors
    ///   (<c>lineStart</c>, <c>cursor</c>). Track the most recent
    ///   <c>Allowed</c> opportunity. On overflow snap back; on
    ///   <c>Mandatory</c> emit; trim trailing control glyphs from
    ///   each emitted slice (CRLF: trims both CR + LF since both
    ///   tag as <c>IsMandatoryControl</c>).</item>
    /// </list>
    ///
    /// <para><b>Cycle 3a white-space behavior.</b> Cycle 3a does
    /// NOT preprocess CSS <c>white-space</c> — input is wrapped
    /// AS-IS. Multiple consecutive spaces stay as multiple glyphs
    /// (no collapsing). Leading + trailing whitespace is preserved.
    /// True CSS <c>white-space: normal</c> (collapse + trim) +
    /// <c>pre</c>/<c>pre-wrap</c>/<c>pre-line</c>/<c>nowrap</c>
    /// variants ship in cycle 3b. Until then, callers are expected
    /// to feed pre-collapsed text or accept the AS-IS behavior; the
    /// <see cref="LineFragment.EndsWithMandatoryBreak"/> flag still
    /// distinguishes paragraph-end from soft-wrap regardless of
    /// white-space mode.</para>
    ///
    /// <para><b>Other cycle 3a deferrals.</b> See
    /// <see cref="LineFragment"/> XML doc for the full list —
    /// hyphenation, overflow-wrap, word-break, text-align,
    /// vertical-align, RTL fragment-level reversal — all cycle 3b/c.</para>
    /// </summary>
    /// <param name="sourceTextRuns">The original source runs passed
    /// to <see cref="Itemize"/> + <see cref="Shape"/>. Used to rebuild
    /// the concatenated text for UAX #14 line-break analysis.</param>
    /// <param name="shapedRuns">The output of <see cref="Shape"/> for
    /// the same source runs.</param>
    /// <param name="availableInlineSize">The maximum inline-axis size
    /// of a line, in CSS px. Glyphs whose cumulative advance exceeds
    /// this value are wrapped to a new line at the most recent UAX #14
    /// <c>Allowed</c> break. Must be positive + finite.</param>
    /// <param name="whiteSpace">The CSS Text L3 <c>white-space</c>
    /// property value that controls wrap-time semantics for this
    /// pass. Default <see cref="WhiteSpace.Normal"/>. Cycle 3b
    /// sub-cycle 1 honors:
    /// <list type="bullet">
    ///   <item><see cref="WhiteSpace.NoWrap"/> + <see cref="WhiteSpace.Pre"/>
    ///   suppress wrapping at UAX #14 <c>Allowed</c> opportunities —
    ///   only <c>Mandatory</c> breaks split the line.</item>
    ///   <item><see cref="WhiteSpace.Normal"/>, <see cref="WhiteSpace.PreWrap"/>,
    ///   <see cref="WhiteSpace.PreLine"/> all wrap at <c>Allowed</c> +
    ///   <c>Mandatory</c> opportunities.</item>
    /// </list>
    /// Whitespace COLLAPSING is a separate concern handled by
    /// <see cref="PreprocessWhitespace(string, WhiteSpace)"/> BEFORE
    /// <see cref="Itemize"/> + <see cref="Shape"/> are called — Wrap
    /// operates on already-shaped glyphs, so it can't collapse
    /// post-hoc. Callers are expected to apply the preprocessor at
    /// TextRun construction time.</param>
    /// <param name="cancellationToken">Checked at method entry, after
    /// each expensive loop pass (concat-rebuild, FindBreaks,
    /// coherence-validation, flat-glyph build, wrap loop), and
    /// before tail emission. The check granularity is per-shaped-run
    /// during validation/flattening + per-line during wrap.</param>
    /// <returns>One <see cref="LineFragment"/> per wrapped line in
    /// document order. Empty array when <paramref name="shapedRuns"/>
    /// is empty or contains only zero-glyph runs.</returns>
    /// <exception cref="ArgumentNullException">A required argument is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="availableInlineSize"/> is non-positive or
    /// non-finite; or <paramref name="whiteSpace"/> is not a defined
    /// <see cref="WhiteSpace"/> value.</exception>
    /// <exception cref="ArgumentException">A shaped run's
    /// <see cref="ShapedRun.Source"/> indexes outside
    /// <paramref name="sourceTextRuns"/>; or a glyph's
    /// <see cref="ShapedGlyph.Cluster"/> is out of range; or a
    /// glyph's <see cref="ShapedGlyph.XAdvance"/> is non-finite or
    /// negative; or a shaped run's UTF-16 range exceeds the
    /// concatenated source text.</exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.</exception>
    public static LineFragment[] Wrap(
        IReadOnlyList<TextRun> sourceTextRuns,
        IReadOnlyList<ShapedRun> shapedRuns,
        double availableInlineSize,
        WhiteSpace whiteSpace = WhiteSpace.Normal,
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
        if (whiteSpace is not (WhiteSpace.Normal
            or WhiteSpace.Pre
            or WhiteSpace.NoWrap
            or WhiteSpace.PreWrap
            or WhiteSpace.PreLine))
        {
            throw new ArgumentOutOfRangeException(nameof(whiteSpace),
                whiteSpace,
                "LineBuilder.Wrap: whiteSpace must be a defined WhiteSpace value.");
        }

        // Per PR #34 review fix — check cancellation at entry, before
        // the expensive concat rebuild + FindBreaks + flat-build + wrap
        // loops. Pre-cancelled tokens fast-path out.
        cancellationToken.ThrowIfCancellationRequested();

        // Cycle 3b — Pre + NoWrap suppress wrapping at UAX #14 Allowed
        // opportunities; only Mandatory breaks split lines.
        var wrapsAtAllowed = whiteSpace is not (WhiteSpace.Pre or WhiteSpace.NoWrap);

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
        cancellationToken.ThrowIfCancellationRequested();

        var concatBuf = new StringBuilder(concatTotal);
        for (var i = 0; i < sourceTextRuns.Count; i++)
        {
            concatBuf.Append(sourceTextRuns[i].Text);
        }
        var concatText = concatBuf.ToString();

        cancellationToken.ThrowIfCancellationRequested();

        // breaks[i] = opportunity AFTER UTF-16 code unit i. Final
        // entry is always Mandatory per LB3.
        var breaks = LineBreakAlgorithm.FindBreaks(concatText.AsSpan());

        cancellationToken.ThrowIfCancellationRequested();

        // Coherence validation pass + total glyph count.
        var totalGlyphs = 0;
        for (var r = 0; r < shapedRuns.Count; r++)
        {
            var shaped = shapedRuns[r];
            var src = shaped.Source;
            if ((uint)src.SourceTextRunIndex >= (uint)sourceTextRuns.Count)
            {
                throw new ArgumentException(
                    $"LineBuilder.Wrap: shapedRuns[{r}].Source.SourceTextRunIndex={src.SourceTextRunIndex} is out of range for sourceTextRuns (count={sourceTextRuns.Count}). Did the shaped runs come from a different source list?",
                    nameof(shapedRuns));
            }
            if (src.Utf16Start < 0 || src.Utf16Length < 0 ||
                (long)src.Utf16Start + src.Utf16Length > concatTotal)
            {
                throw new ArgumentException(
                    $"LineBuilder.Wrap: shapedRuns[{r}].Source range [Utf16Start={src.Utf16Start}, Utf16Length={src.Utf16Length}] is out of bounds for the concatenated source text (length={concatTotal}).",
                    nameof(shapedRuns));
            }
            var glyphs = shaped.Glyphs;
            for (var g = 0; g < glyphs.Length; g++)
            {
                var glyph = glyphs[g];
                if (glyph.Cluster < 0 || glyph.Cluster >= concatTotal)
                {
                    throw new ArgumentException(
                        $"LineBuilder.Wrap: shapedRuns[{r}].Glyphs[{g}].Cluster={glyph.Cluster} is out of bounds [0, {concatTotal}).",
                        nameof(shapedRuns));
                }
                if (!float.IsFinite(glyph.XAdvance) || glyph.XAdvance < 0)
                {
                    throw new ArgumentException(
                        $"LineBuilder.Wrap: shapedRuns[{r}].Glyphs[{g}].XAdvance={glyph.XAdvance} is non-finite or negative; cycle 3a expects HarfBuzz output with finite, non-negative advances.",
                        nameof(shapedRuns));
                }
            }
            totalGlyphs += glyphs.Length;
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (totalGlyphs == 0)
        {
            return Array.Empty<LineFragment>();
        }

        // Build the flat glyph array. For each glyph, compute the
        // cluster-end UTF-16 position so the break-opportunity lookup
        // is breaks[clusterEnd-1] — the opportunity AFTER the cluster,
        // NOT after the cluster's first code unit. Surrogate pairs +
        // combining marks + ligatures all map correctly.
        var flat = new FlatGlyph[totalGlyphs];
        var flatIdx = 0;
        for (var r = 0; r < shapedRuns.Count; r++)
        {
            var shaped = shapedRuns[r];
            var glyphs = shaped.Glyphs;
            var runUtf16End = shaped.Source.Utf16Start + shaped.Source.Utf16Length;
            var isRtl = shaped.Source.IsRtl;

            for (var g = 0; g < glyphs.Length; g++)
            {
                var glyph = glyphs[g];

                int clusterEnd;
                if (!isRtl)
                {
                    // LTR: clusters monotonically increase. Next-cluster
                    // start (= this cluster's end) is glyphs[g+1].Cluster
                    // for non-last glyphs, or runUtf16End for the last.
                    clusterEnd = (g + 1 < glyphs.Length)
                        ? glyphs[g + 1].Cluster
                        : runUtf16End;
                }
                else
                {
                    // RTL: glyphs are in HarfBuzz visual (reversed) order.
                    // Cluster-end is harder to compute correctly because
                    // it requires looking at the previous-in-source glyph,
                    // which for ligatures + combining marks isn't trivial.
                    // Cycle 3a uses (cluster + 1) as a fallback — correct
                    // for single-codeunit clusters; under-detects breaks
                    // at multi-codeunit RTL cluster boundaries (Arabic
                    // ligatures across surrogate pairs etc.). Cycle 3c's
                    // RTL fragment-level reversal will refine this with
                    // proper source-cluster span tracking.
                    clusterEnd = glyph.Cluster + 1;
                }

                var breakIdx = clusterEnd - 1;
                var opp = LineBreakOpportunity.Prohibited;
                if ((uint)breakIdx < (uint)breaks.Length)
                {
                    opp = breaks[breakIdx];
                }

                // Tag mandatory-line-break control glyphs (LF, CR, VT,
                // FF, NEL, LS, PS). The painter must NOT emit glyph
                // data for these; the wrap loop trims them off the
                // drawable slice.
                var isMandatoryControl = false;
                if ((uint)glyph.Cluster < (uint)concatTotal)
                {
                    isMandatoryControl = IsMandatoryLineBreakControl(
                        concatText[glyph.Cluster]);
                }

                flat[flatIdx++] = new FlatGlyph(
                    RunIdx: r,
                    GlyphIdxInRun: g,
                    Advance: glyph.XAdvance,
                    Opportunity: opp,
                    IsMandatoryControl: isMandatoryControl);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Wrap loop.
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
                EmitDrawableRange(output, flat, lineStart, lastAllowed,
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
                // Trim trailing mandatory-control glyphs from the
                // drawable range. Handles single-LF, lone CR, lone
                // NEL/PS/etc. cleanly; CRLF strips both since both
                // CR + LF tag as IsMandatoryControl.
                var drawableEnd = cursor;
                while (drawableEnd >= lineStart
                    && flat[drawableEnd].IsMandatoryControl)
                {
                    drawableEnd--;
                }

                EmitDrawableRange(output, flat, lineStart, drawableEnd,
                    endsWithMandatoryBreak: true);
                lineStart = cursor + 1;
                lineAdvance = 0;
                lastAllowed = -1;
                cancellationToken.ThrowIfCancellationRequested();
            }
            else if (item.Opportunity == LineBreakOpportunity.Allowed
                && wrapsAtAllowed)
            {
                // Cycle 3b — Pre + NoWrap suppress Allowed-break
                // wrapping. Skip recording the candidate so the
                // wrap loop's snap-back never fires; only Mandatory
                // breaks split the line.
                lastAllowed = cursor;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Tail — any glyphs from lineStart..totalGlyphs-1 that didn't
        // hit a Mandatory or overflow snap-back. Per LB3 the last
        // codepoint's opportunity is always Mandatory, so this only
        // fires if the last glyph's cluster didn't map to the
        // mandatory entry (e.g., last glyph's cluster span ends past
        // breaks.Length due to coherence drift, or RTL cluster-end
        // approximation under-detected the break).
        if (lineStart < totalGlyphs)
        {
            EmitDrawableRange(output, flat, lineStart, totalGlyphs - 1,
                endsWithMandatoryBreak: false);
        }

        return output.ToArray();
    }

    /// <summary>UAX #14 hard-line-break control characters that
    /// callers must not draw. Trimmed off the end of each emitted
    /// drawable slice.</summary>
    private static bool IsMandatoryLineBreakControl(char c) =>
        c == '\u000A' // LF
        || c == '\u000B' // VT
        || c == '\u000C' // FF
        || c == '\u000D' // CR
        || c == '\u0085' // NEL
        || c == '\u2028' // LS
        || c == '\u2029'; // PS

    /// <summary>Per Phase 3 Task 9 cycle 3b sub-cycle 1 — apply CSS
    /// Text L3 §4.1 white-space processing rules to a source text
    /// string. Callers (typically the integrating <c>InlineLayouter</c>
    /// in Task 10) call this BEFORE constructing
    /// <see cref="TextRun"/>s — Wrap operates on already-shaped
    /// glyphs and can't collapse post-hoc.
    ///
    /// <para><b>Algorithm per mode (CSS Text L3 §3 + §4.1):</b></para>
    /// <list type="bullet">
    ///   <item><see cref="WhiteSpace.Normal"/> + <see cref="WhiteSpace.NoWrap"/>:
    ///   collapse all whitespace runs (SP/TAB/LF/CR/FF) into a
    ///   single SP. Strip leading + trailing whitespace.</item>
    ///   <item><see cref="WhiteSpace.Pre"/> + <see cref="WhiteSpace.PreWrap"/>:
    ///   pass through unchanged.</item>
    ///   <item><see cref="WhiteSpace.PreLine"/>: collapse SP+TAB runs
    ///   to a single SP; preserve LF + CR (segment breaks). Strips
    ///   trailing SP at segment ends per §4.1.2 "remove end-of-line
    ///   spaces".</item>
    /// </list>
    ///
    /// <para><b>Cycle 3b sub-cycle 1 simplifications.</b> The CSS
    /// Text L3 §4.1.1 segment-break-transformation rules
    /// (CR-LF→LF, etc.) are NOT applied here — that pass belongs in
    /// the HTML preprocessor (cycle 3b sub-cycle 2). Likewise the
    /// "preserved tab" tab-size handling for Pre-mode is deferred.
    /// Cycle 3b ships the most common cases (Normal + NoWrap collapse,
    /// Pre/PreWrap pass-through, PreLine SP-collapse) which cover
    /// 99% of v1 invoice / report content.</para>
    /// </summary>
    /// <param name="text">The source text — typically from a single
    /// box-tree TextRun before bidi/itemize. Surrogate pairs are
    /// preserved as-is (CSS whitespace chars are all BMP).</param>
    /// <param name="mode">The CSS <c>white-space</c> value to apply.</param>
    /// <returns>Preprocessed text ready to feed into a
    /// <see cref="TextRun"/>. May share the input reference when
    /// <paramref name="mode"/> is pass-through (Pre/PreWrap).</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mode"/> is not a defined
    /// <see cref="WhiteSpace"/> value.</exception>
    public static string PreprocessWhitespace(string text, WhiteSpace mode)
    {
        ArgumentNullException.ThrowIfNull(text);
        return mode switch
        {
            WhiteSpace.Pre or WhiteSpace.PreWrap => text,
            WhiteSpace.PreLine => CollapseSpacesPreserveBreaks(text),
            WhiteSpace.Normal or WhiteSpace.NoWrap => CollapseAllWhitespace(text),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "PreprocessWhitespace: mode must be a defined WhiteSpace value."),
        };
    }

    /// <summary>White-space:normal / nowrap — collapse all SP/TAB/LF/
    /// CR/FF runs to a single SP + strip leading/trailing SP.</summary>
    private static string CollapseAllWhitespace(string text)
    {
        if (text.Length == 0) return text;
        var sb = new StringBuilder(text.Length);
        // inWs initialized to true so leading whitespace is stripped.
        var inWs = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsCssWhiteSpace(c))
            {
                if (!inWs)
                {
                    sb.Append(' ');
                    inWs = true;
                }
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }
        // Strip trailing space if present.
        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    /// <summary>White-space:pre-line — collapse SP/TAB runs to a
    /// single SP but preserve LF + CR segment breaks. Strips trailing
    /// SP at segment ends per §4.1.2.</summary>
    private static string CollapseSpacesPreserveBreaks(string text)
    {
        if (text.Length == 0) return text;
        var sb = new StringBuilder(text.Length);
        var inSpaceRun = true; // strip leading SP
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == ' ' || c == '\u0009' || c == '\u000C')
            {
                if (!inSpaceRun)
                {
                    sb.Append(' ');
                    inSpaceRun = true;
                }
            }
            else if (c == '\u000A' || c == '\u000D')
            {
                // Segment break — strip a pending trailing SP, append
                // the break, reset for the new segment's leading-SP
                // strip.
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                {
                    sb.Length--;
                }
                sb.Append(c);
                inSpaceRun = true;
            }
            else
            {
                sb.Append(c);
                inSpaceRun = false;
            }
        }
        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length--;
        }
        return sb.ToString();
    }

    /// <summary>CSS Text L3 §3.1 "white space" set for the collapse
    /// rules. Excludes U+00A0 NBSP (non-collapsing) + U+200B ZWSP
    /// (zero-width).</summary>
    private static bool IsCssWhiteSpace(char c) =>
        c == ' ' // SPACE
        || c == '\u0009' // TAB
        || c == '\u000A' // LF
        || c == '\u000D' // CR
        || c == '\u000C'; // FF

    /// <summary>Slice a global glyph range <c>[start, end]</c>
    /// (inclusive on both ends) into per-run
    /// <see cref="ShapedRunSlice"/>s + emit a
    /// <see cref="LineFragment"/>. Drawable-only — caller is
    /// expected to have already trimmed any trailing control glyphs.
    /// When <c>start &gt; end</c>, emits an empty
    /// <see cref="LineFragment"/> (e.g., a lone LF on its own line
    /// after trimming).</summary>
    private static void EmitDrawableRange(
        List<LineFragment> output,
        FlatGlyph[] flat, int start, int end,
        bool endsWithMandatoryBreak)
    {
        if (start > end)
        {
            // Empty drawable range (a control-only line, e.g., lone LF).
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
    /// shaped runs, indexed by global glyph position. <see cref="IsMandatoryControl"/>
    /// is set for glyphs whose source codepoint is a UAX #14 hard-
    /// line-break control (LF, CR, VT, FF, NEL, LS, PS) — these are
    /// trimmed off the end of each emitted drawable slice.</summary>
    private readonly record struct FlatGlyph(
        int RunIdx,
        int GlyphIdxInRun,
        float Advance,
        LineBreakOpportunity Opportunity,
        bool IsMandatoryControl);
}
