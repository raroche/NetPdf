// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using NetPdf.Text.Bidi;
using NetPdf.Text.Hyphenation;
using NetPdf.Text.LineBreaking;
using NetPdf.Text.Segmentation;
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
    /// <param name="cancellationToken">Per Phase 3 Task 10 cycle 1
    /// review fix — cancellation is observed at method entry, after
    /// per-run total-length accumulation, after concat + source-map
    /// fill (single pass through total UTF-16 length), and after
    /// bidi resolution. Long inputs (very wide blocks of text or
    /// pathological documents) terminate cooperatively instead of
    /// running an uninterrupted bidi pass.</param>
    /// <returns>Itemized runs covering the full text in document
    /// order. Each run's <see cref="ItemizedRun.Utf16Start"/> +
    /// <see cref="ItemizedRun.Utf16Length"/> indexes into the
    /// concatenated input text (= the source TextRuns joined).</returns>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.</exception>
    public static ItemizedRun[] Itemize(
        IReadOnlyList<TextRun> textRuns,
        ParagraphDirection paragraphDirection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textRuns);
        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();

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
        cancellationToken.ThrowIfCancellationRequested();

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

            // Inline-atomic-boxes cycle — an ATOMIC inline box (an inline `<img>`) is NOT text-shaped.
            // Emit one synthetic glyph whose advance is the atomic's used width so the wrap pass treats
            // it as a single non-breakable unit (the source `U+FFFC` carries UAX #14 break opportunities
            // around it); the painter skips the glyph + paints the box from its own emitted fragment.
            if (sourceTextRuns[run.SourceTextRunIndex].Atomic is { } atomic)
            {
                var atomicAdvance = (float)Math.Max(0, atomic.AdvancePx);
                output[runIdx] = new ShapedRun(
                    run,
                    new[]
                    {
                        new ShapedGlyph(
                            GlyphId: 0, XAdvance: atomicAdvance, YAdvance: 0,
                            XOffset: 0, YOffset: 0, Cluster: run.Utf16Start),
                    },
                    atomic.AdvancePx,
                    atomic);
                continue;
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
    ///   <c>Mandatory</c> opportunities. Normal / NoWrap / PreLine
    ///   also trim trailing collapsible-whitespace glyphs (SP/TAB)
    ///   off the drawable slice when wrapping at an Allowed break
    ///   per §4.1.2 ("remove end-of-line spaces").</item>
    /// </list>
    /// Whitespace COLLAPSING is a separate concern handled by
    /// <see cref="PreprocessWhitespace(string, WhiteSpace)"/> +
    /// <see cref="PreprocessTextRuns"/> BEFORE <see cref="Itemize"/>
    /// + <see cref="Shape"/> are called — Wrap operates on already-
    /// shaped glyphs, so it can't collapse post-hoc.
    ///
    /// <para><b>Per-run policy is a stopgap.</b> Cycle 3b sub-cycle 1
    /// applies one <see cref="WhiteSpace"/> mode to the WHOLE inline
    /// pass — adequate for paragraphs whose every descendant inherits
    /// the same <c>white-space</c> property (the common case). Mixed
    /// inline descendants (e.g.,
    /// <c>&lt;span style="white-space:nowrap"&gt;</c> inside
    /// <c>white-space:normal</c> text) require per-source-TextRun
    /// white-space carrying through to the wrap pass — scheduled for
    /// Task 10's <c>InlineLayouter</c> integration. The single-arg
    /// is a stopgap, not the long-term API.</para></param>
    /// <param name="overflowWrap">CSS Text L3 §5.1
    /// <c>overflow-wrap</c> property value. Default
    /// <see cref="OverflowWrap.Normal"/>. <see cref="OverflowWrap.Anywhere"/>
    /// forces a per-glyph break when the line would overflow + no
    /// UAX #14 Allowed candidate exists, BUT only when wrapping is
    /// permitted by <paramref name="whiteSpace"/> (i.e., NOT under
    /// <see cref="WhiteSpace.Pre"/>/<see cref="WhiteSpace.NoWrap"/>).
    /// Cycle 3b sub-cycle 2.</param>
    /// <param name="wordBreak">CSS Text L3 §5.2 <c>word-break</c>
    /// property value. Default <see cref="WordBreak.Normal"/>.
    /// <see cref="WordBreak.BreakAll"/> upgrades selected Prohibited
    /// glyph boundaries to Allowed candidates — restricted to
    /// grapheme cluster boundaries (UAX #29 §3.1) + boundaries that
    /// are NOT adjacent to ZWJ (U+200D), WJ (U+2060), or NBSP
    /// (U+00A0). Combining-mark + emoji-ZWJ + flag-pair sequences
    /// stay atomic. <see cref="WordBreak.KeepAll"/> is recognized
    /// but has no observable effect for Latin/Cyrillic/Greek content
    /// (CJK semantics activate when UAX #24 lands in cycle 4).
    /// Cycle 3b sub-cycle 2.</param>
    ///
    /// <para><b>Production wiring (Task 10 cycles 2-4 shipped).</b>
    /// The CSS pipeline (<c>properties.json</c> →
    /// <c>KeywordResolver</c> → <c>ComputedStyle</c>) for
    /// <paramref name="overflowWrap"/>, <paramref name="wordBreak"/>,
    /// and <paramref name="hyphens"/> shipped in Task 10 cycle 2.
    /// <see cref="InlineLayouter.LayoutPerRun"/> reads each source-
    /// TextRun's <see cref="InlineTextPolicy"/> via
    /// <see cref="InlineTextPolicyMaterializer.ReadInlineTextPolicy"/>
    /// + passes the per-run array to <see cref="LineBuilder.Wrap"/>
    /// (cycle 3d sub-cycles 1-4). The uniform <paramref name="overflowWrap"/>
    /// / <paramref name="wordBreak"/> / <paramref name="hyphens"/>
    /// arguments still apply when <c>inlineTextPolicyPerRun</c> is
    /// null (direct/test callers); per-run values take precedence
    /// when supplied.</para>
    /// <param name="hyphens">CSS Text L3 §6.1 <c>hyphens</c> property
    /// value. Default <see cref="Hyphens.Manual"/> (CSS default).
    /// <see cref="Hyphens.None"/> ignores soft-hyphens + disables
    /// auto-hyphenation. <see cref="Hyphens.Manual"/> treats source
    /// soft-hyphens (U+00AD) as break opportunities.
    /// <see cref="Hyphens.Auto"/> additionally applies Liang-pattern
    /// auto-hyphenation. Cycle 3b sub-cycle 3.
    ///
    /// <para><b>CSS-property pipeline (shipped via Task 10 cycles
    /// 2-4).</b> The full pipeline for <c>hyphens</c> is in place:
    /// <c>properties.json</c> definition, <c>KeywordResolver</c>
    /// mapping (none/manual/auto), <c>ComputedStyle</c>
    /// materialization via <see cref="InlineTextPolicyMaterializer.ReadInlineTextPolicy"/>,
    /// + per-source-TextRun plumbing through
    /// <see cref="InlineLayouter.LayoutPerRun"/> using the
    /// <c>inlineTextPolicyPerRun</c> parameter below. Mixed-mode
    /// descendants like <c>&lt;span style="hyphens:none"&gt;</c>
    /// inside <c>hyphens:auto</c> text work end-to-end as of
    /// cycle 3d sub-cycle 4. The uniform <paramref name="hyphens"/>
    /// argument here applies when no per-run array is supplied
    /// (direct/test callers).</para></param>
    /// <param name="hyphenator">Optional Liang
    /// <see cref="Hyphenator"/> for <see cref="Hyphens.Auto"/>. When
    /// <see langword="null"/> + Auto mode, falls back to
    /// <see cref="EnUsHyphenation.Default"/> (en-US patterns).
    /// Per-language pattern routing lands when Task 10's
    /// <c>InlineLayouter</c> integrates per-source-TextRun language.
    /// Cycle 3b sub-cycle 3.</param>
    /// <param name="cancellationToken">Checked at method entry, after
    /// each expensive loop pass (concat-rebuild, FindBreaks,
    /// coherence-validation, flat-glyph build, wrap loop), and
    /// before tail emission. The check granularity is per-shaped-run
    /// during validation/flattening + per-line during wrap.</param>
    /// <param name="inlineTextPolicyPerRun">Per Phase 3 Task 10
    /// cycle 3d sub-cycle 2 review Rec #4 — optional per-source-
    /// TextRun <see cref="InlineTextPolicy"/> override. Replaces
    /// the cycle 3c <c>whiteSpacePerRun</c> + cycle 3d sub-cycle 2
    /// <c>overflowWrapPerRun</c> parallel arrays with a single
    /// coherent policy struct per source run. When supplied, MUST
    /// have one entry per source run; the wrap loop reads each
    /// glyph's source-run-index to look up its run-specific
    /// policy.
    ///
    /// <para>Active per-run dimensions:</para>
    /// <list type="bullet">
    ///   <item><see cref="InlineTextPolicy.WhiteSpace"/> — drives
    ///   per-glyph UAX #14 Allowed-opportunity downgrade for
    ///   NoWrap/Pre runs + per-glyph IsBreakSpace tag (only
    ///   collapse modes trim).</item>
    ///   <item><see cref="InlineTextPolicy.OverflowWrap"/> — gates
    ///   the <c>overflow-wrap: anywhere</c> forced-break fallback
    ///   by source run; cross-run breaks additionally require at
    ///   least one side to be wrap-friendly per
    ///   <see cref="InlineTextPolicy.WhiteSpace"/>.</item>
    ///   <item><see cref="InlineTextPolicy.WordBreak"/> (sub-cycle
    ///   3) — drives per-glyph BreakAll upgrade. Glyphs in
    ///   BreakAll source runs get their Prohibited boundaries
    ///   upgraded to Allowed at grapheme cluster boundaries. The
    ///   cross-run BreakAll boundary uses "either side may opt in"
    ///   (mirrors OverflowWrap's cross-run rule). KeepAll is read
    ///   but NOT yet honored: behaves like Normal (no breaks
    ///   suppressed), and <see cref="InlineLayouter.LayoutPerRun"/>
    ///   throws on KeepAll mismatch to fail loud.</item>
    ///   <item><see cref="InlineTextPolicy.Hyphens"/> (sub-cycle 4)
    ///   — drives per-source-run hyphenation. The soft-hyphen
    ///   demotion (Hyphens=None) and Liang application (per word,
    ///   Hyphens=Auto only) consult the per-run array via a
    ///   position→source-run-index map built lazily. The sub-cycle 3
    ///   defense-in-depth guard (per-run Hyphens must equal global)
    ///   is removed; per-run mismatches are accepted.</item>
    /// </list>
    ///
    /// <para>The uniform <paramref name="wordBreak"/> +
    /// <paramref name="hyphens"/> arguments still apply when this
    /// array is null. When supplied, the per-run values take
    /// precedence for the dimensions noted above.</para></param>
    /// <param name="intrinsicSizingMode">Per Phase 3 Task 12 sub-cycle
    /// 5 hardening Finding 5 — when <see langword="true"/>,
    /// <see cref="OverflowWrap.BreakWord"/> is downgraded to
    /// <see cref="OverflowWrap.Normal"/> so the per-glyph forced-
    /// break fallback doesn't fire during the auto-table-layout
    /// speculative min-content pass. Per CSS Text L3 §5.1
    /// <c>break-word</c>'s soft opportunities do NOT count for
    /// min-content sizing (the column min-content remains the full
    /// word width). <see cref="OverflowWrap.Anywhere"/> opportunities
    /// continue to count (the spec carves out anywhere as the only
    /// soft-opportunity source that contributes to intrinsic sizing).
    /// Defaults to <see langword="false"/> — the line-wrap pass for
    /// final layout always honors break-word's glyph-boundary
    /// fallback.</param>
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
        OverflowWrap overflowWrap = OverflowWrap.Normal,
        WordBreak wordBreak = WordBreak.Normal,
        Hyphens hyphens = Hyphens.Manual,
        Hyphenator? hyphenator = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<InlineTextPolicy>? inlineTextPolicyPerRun = null,
        bool intrinsicSizingMode = false)
    {
        ArgumentNullException.ThrowIfNull(sourceTextRuns);
        ArgumentNullException.ThrowIfNull(shapedRuns);
        // Per Phase 3 Task 10 cycle 3d sub-cycle 2 review Rec #4 —
        // single per-source-run InlineTextPolicy array. Length must
        // match sourceTextRuns count; every entry's WhiteSpace +
        // OverflowWrap fields must be defined enum values. (WordBreak
        // and Hyphens fields are validated for sanity even though
        // they're not yet honored per-run — sub-cycle 3+ will plumb
        // them through.)
        if (inlineTextPolicyPerRun is not null
            && inlineTextPolicyPerRun.Count != sourceTextRuns.Count)
        {
            throw new ArgumentException(
                $"LineBuilder.Wrap: inlineTextPolicyPerRun length ({inlineTextPolicyPerRun.Count}) " +
                $"must match sourceTextRuns count ({sourceTextRuns.Count}).",
                nameof(inlineTextPolicyPerRun));
        }
        if (inlineTextPolicyPerRun is not null)
        {
            for (var i = 0; i < inlineTextPolicyPerRun.Count; i++)
            {
                var p = inlineTextPolicyPerRun[i];
                if (p.WhiteSpace is not (WhiteSpace.Normal
                    or WhiteSpace.Pre
                    or WhiteSpace.NoWrap
                    or WhiteSpace.PreWrap
                    or WhiteSpace.PreLine
                    or WhiteSpace.BreakSpaces))
                {
                    throw new ArgumentException(
                        $"LineBuilder.Wrap: inlineTextPolicyPerRun[{i}].WhiteSpace = {p.WhiteSpace} " +
                        $"is not a defined WhiteSpace value.",
                        nameof(inlineTextPolicyPerRun));
                }
                if (p.OverflowWrap is not (OverflowWrap.Normal
                    or OverflowWrap.Anywhere or OverflowWrap.BreakWord))
                {
                    throw new ArgumentException(
                        $"LineBuilder.Wrap: inlineTextPolicyPerRun[{i}].OverflowWrap = {p.OverflowWrap} " +
                        $"is not a defined OverflowWrap value.",
                        nameof(inlineTextPolicyPerRun));
                }
                if (p.WordBreak is not (WordBreak.Normal
                    or WordBreak.BreakAll or WordBreak.KeepAll))
                {
                    throw new ArgumentException(
                        $"LineBuilder.Wrap: inlineTextPolicyPerRun[{i}].WordBreak = {p.WordBreak} " +
                        $"is not a defined WordBreak value.",
                        nameof(inlineTextPolicyPerRun));
                }
                if (p.Hyphens is not (Hyphens.None or Hyphens.Manual or Hyphens.Auto))
                {
                    throw new ArgumentException(
                        $"LineBuilder.Wrap: inlineTextPolicyPerRun[{i}].Hyphens = {p.Hyphens} " +
                        $"is not a defined Hyphens value.",
                        nameof(inlineTextPolicyPerRun));
                }
                // Cycle 3d sub-cycle 4 — per-source-run Hyphens is
                // now honored. The defense-in-depth guard from
                // sub-cycle 3 (Hyphens must equal global) is
                // removed; the hyphenation pipeline below reads
                // per-position Hyphens from inlineTextPolicyPerRun.
            }
        }
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
            or WhiteSpace.PreLine
            or WhiteSpace.BreakSpaces))
        {
            throw new ArgumentOutOfRangeException(nameof(whiteSpace),
                whiteSpace,
                "LineBuilder.Wrap: whiteSpace must be a defined WhiteSpace value.");
        }
        if (overflowWrap is not (OverflowWrap.Normal
            or OverflowWrap.Anywhere or OverflowWrap.BreakWord))
        {
            throw new ArgumentOutOfRangeException(nameof(overflowWrap),
                overflowWrap,
                "LineBuilder.Wrap: overflowWrap must be a defined OverflowWrap value.");
        }

        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
        // under intrinsic sizing (auto-table-layout's speculative
        // min-content pass), BreakWord opportunities don't count
        // (CSS Text L3 §5.1: "values that introduce additional break
        // opportunities (other than UAX #14) are considered for
        // min-content sizing only if overflow-wrap: anywhere is in
        // effect"). Downgrade BreakWord → Normal so the per-glyph
        // forced-break fallback doesn't fire during intrinsic sizing.
        // Anywhere remains honored (its opportunities DO count for
        // min-content). This downgrade is the cleanest way to honor
        // the spec without forking the wrap loop.
        if (intrinsicSizingMode && overflowWrap == OverflowWrap.BreakWord)
        {
            overflowWrap = OverflowWrap.Normal;
        }
        if (intrinsicSizingMode && inlineTextPolicyPerRun is not null)
        {
            // Rebuild the per-run array, downgrading BreakWord → Normal.
            // Allocate only when at least one run carries BreakWord —
            // saves allocations in the common case where the caller
            // passes intrinsicSizingMode=true but no run uses
            // break-word.
            var hasBreakWord = false;
            for (var i = 0; i < inlineTextPolicyPerRun.Count; i++)
            {
                if (inlineTextPolicyPerRun[i].OverflowWrap == OverflowWrap.BreakWord)
                {
                    hasBreakWord = true;
                    break;
                }
            }
            if (hasBreakWord)
            {
                var downgraded = new InlineTextPolicy[inlineTextPolicyPerRun.Count];
                for (var i = 0; i < inlineTextPolicyPerRun.Count; i++)
                {
                    var p = inlineTextPolicyPerRun[i];
                    downgraded[i] = p.OverflowWrap == OverflowWrap.BreakWord
                        ? p with { OverflowWrap = OverflowWrap.Normal }
                        : p;
                }
                inlineTextPolicyPerRun = downgraded;
            }
        }
        if (wordBreak is not (WordBreak.Normal or WordBreak.BreakAll or WordBreak.KeepAll))
        {
            throw new ArgumentOutOfRangeException(nameof(wordBreak),
                wordBreak,
                "LineBuilder.Wrap: wordBreak must be a defined WordBreak value.");
        }
        if (hyphens is not (Hyphens.None or Hyphens.Manual or Hyphens.Auto))
        {
            throw new ArgumentOutOfRangeException(nameof(hyphens),
                hyphens,
                "LineBuilder.Wrap: hyphens must be a defined Hyphens value.");
        }

        // Per PR #34 review fix — check cancellation at entry, before
        // the expensive concat rebuild + FindBreaks + flat-build + wrap
        // loops. Pre-cancelled tokens fast-path out.
        cancellationToken.ThrowIfCancellationRequested();

        // Cycle 3b — Pre + NoWrap suppress wrapping at UAX #14 Allowed
        // opportunities; only Mandatory breaks split lines.
        //
        // Per Phase 3 Task 10 cycle 3c review Rec #2 + Copilot #2/#3
        // — when whiteSpacePerRun is supplied the global gate must
        // not unconditionally suppress wraps for the WHOLE pass. The
        // per-glyph downgrade below (lines 931-943) converts Allowed
        // → Prohibited for every glyph in a NoWrap/Pre source run;
        // setting `wrapsAtAllowed = true` whenever per-run mode is
        // active lets the per-glyph filter authoritatively decide.
        // (Without this, a leading NoWrap source run made
        // `whiteSpace = NoWrap` reach this seam, which suppressed
        // wraps in a TRAILING Normal source run too — exactly the
        // Normal-after-NoWrap order bug the review flagged.)
        //
        // When inlineTextPolicyPerRun is null, the original cycle-3b
        // semantics apply: the uniform `whiteSpace` argument decides.
        var wrapsAtAllowed = inlineTextPolicyPerRun is not null
            || whiteSpace is not (WhiteSpace.Pre or WhiteSpace.NoWrap);

        // Cycle 3b sub-cycle 1 hardening — Normal + NoWrap + PreLine
        // collapse spaces; trailing collapsible whitespace at the
        // wrap point is trimmed off the drawable slice (the SP glyph
        // exists for shaping but doesn't contribute to the line's
        // visible TotalAdvance).
        var collapsesSpaces = whiteSpace is WhiteSpace.Normal
            or WhiteSpace.NoWrap
            or WhiteSpace.PreLine;

        // Cycle 3b sub-cycle 2 — word-break:break-all treats every
        // glyph boundary as a soft-break candidate (overrides UAX #14
        // Prohibited classifications). overflow-wrap:anywhere
        // permits a forced break when overflow occurs + no candidate
        // exists (preserving UAX #14 candidates as preferred — the
        // per-glyph fallback only fires under overflow + no
        // candidate).
        var breakAllGlyphs = wordBreak == WordBreak.BreakAll;
        // Per Phase 3 Task 10 cycle 3d sub-cycle 2 / 3 — when
        // inlineTextPolicyPerRun is supplied, the global
        // allowOverflowAnywhere + breakAllGlyphs flags are true iff
        // ANY source run enables the corresponding feature (we still
        // need to enter the per-glyph code path for those runs).
        // The per-glyph checks at the relevant gates then enforce
        // run-specific semantics. Computing graphemeBreakAfter is
        // also gated on these OR-over-runs values.
        //
        // Per Phase 3 Task 12 sub-cycle 5 hardening Finding 5 —
        // OverflowWrap.BreakWord behaves like Anywhere at LINE-WRAP
        // time (both fire glyph-boundary breaks when no UAX #14
        // Allowed candidate fits). The distinction only matters
        // during intrinsic sizing (where BreakWord opportunities
        // don't count for min-content per CSS Text L3 §5.1) — the
        // TableLayouter's MeasureCellIntrinsicWidths runs its
        // min-content pass under intrinsicSizingMode=true (sub-cycle
        // 5 hardening Finding 5; see the new Wrap parameter).
        var allowOverflowAnywhere =
            overflowWrap is OverflowWrap.Anywhere or OverflowWrap.BreakWord;
        if (inlineTextPolicyPerRun is not null)
        {
            allowOverflowAnywhere = false;
            breakAllGlyphs = false;
            for (var i = 0; i < inlineTextPolicyPerRun.Count; i++)
            {
                var p = inlineTextPolicyPerRun[i];
                if (p.OverflowWrap is OverflowWrap.Anywhere or OverflowWrap.BreakWord)
                    allowOverflowAnywhere = true;
                if (p.WordBreak == WordBreak.BreakAll)
                    breakAllGlyphs = true;
                if (allowOverflowAnywhere && breakAllGlyphs) break;
            }
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

        // Cycle 3b sub-cycle 2 hardening — for word-break:break-all
        // + overflow-wrap:anywhere, forced breaks must land at
        // grapheme cluster boundaries (UAX #29 §3.1) rather than raw
        // glyph indices. Build a sparse boolean lookup
        // graphemeBreakAfter[i] = true iff position (i+1) is a
        // grapheme boundary. The boundary array from
        // GraphemeClusterBreaker.FindBoundaries lists positions
        // [0, ..., concatTotal] in sorted order.
        bool[]? graphemeBreakAfter = null;
        if (breakAllGlyphs || allowOverflowAnywhere)
        {
            var boundaries = GraphemeClusterBreaker.FindBoundaries(
                concatText.AsSpan());
            graphemeBreakAfter = new bool[concatTotal];
            // boundaries always includes 0 + concatTotal. Mark each
            // (boundary - 1) as "break after this index is OK".
            for (var b = 0; b < boundaries.Length; b++)
            {
                var pos = boundaries[b];
                if (pos > 0 && pos <= concatTotal)
                {
                    graphemeBreakAfter[pos - 1] = true;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Cycle 3b sub-cycle 3 — hyphenation candidates.
        // hyphenationAfter[i] = true means a hyphen-break is allowed
        // BETWEEN UTF-16 code unit i and i+1. Built from:
        //   1. Soft-hyphens (U+00AD) in the source — Manual + Auto.
        //   2. Liang pattern auto-hyphenation positions — Auto only.
        //
        // Per PR #37 review fix (User #5): Manual-mode fast path —
        // skip the hyphenationAfter[] allocation entirely when the
        // text contains no U+00AD (the only break source under
        // Manual). Saves an allocation + a scan for the common case
        // of paragraphs without explicit soft-hyphens.
        // Per Phase 3 Task 10 cycle 3d sub-cycle 4 + sub-cycle 4
        // review Finding #3 — per-source-run Hyphens handling.
        // Compute the "any Hyphens != None / any Auto / any None"
        // aggregates from inlineTextPolicyPerRun (or uniform
        // hyphens) FIRST, then build the position→source-run-index
        // map ONLY when actually needed (i.e., when the per-run
        // array is supplied AND the text needs per-position
        // Hyphens decisions — either a soft hyphen present or any
        // Auto run for Liang).
        var anyHyphensNotNone = false;
        var anyHyphensAuto = false;
        var anyHyphensNone = false;
        if (inlineTextPolicyPerRun is not null)
        {
            for (var i = 0; i < inlineTextPolicyPerRun.Count; i++)
            {
                var h = inlineTextPolicyPerRun[i].Hyphens;
                if (h == Hyphens.None) anyHyphensNone = true;
                else
                {
                    anyHyphensNotNone = true;
                    if (h == Hyphens.Auto) anyHyphensAuto = true;
                }
            }
        }
        else
        {
            anyHyphensNotNone = hyphens != Hyphens.None;
            anyHyphensAuto = hyphens == Hyphens.Auto;
            anyHyphensNone = hyphens == Hyphens.None;
        }

        var hasSoftHyphen = concatText.IndexOf('­') >= 0;

        // Sub-cycle 4 review Finding #3 — build posToSrcRun lazily.
        // Skipped entirely for paragraphs without hyphenation needs
        // (mixed WhiteSpace / OverflowWrap / WordBreak don't touch
        // this code path). For large inline content this saves
        // 4 bytes per UTF-16 code unit on common non-hyphenation
        // paths.
        int[]? posToSrcRun = null;
        if (inlineTextPolicyPerRun is not null
            && (hasSoftHyphen || anyHyphensAuto))
        {
            posToSrcRun = new int[concatTotal];
            var p = 0;
            for (var r = 0; r < sourceTextRuns.Count; r++)
            {
                var len = sourceTextRuns[r].Text.Length;
                for (var k = 0; k < len && p + k < concatTotal; k++)
                {
                    posToSrcRun[p + k] = r;
                }
                p += len;
            }
        }

        bool[]? hyphenationAfter = null;
        if (!anyHyphensAuto && !hasSoftHyphen)
        {
            // Fast path — no soft-hyphens + no Liang.
        }
        else if (anyHyphensNotNone)
        {
            hyphenationAfter = ComputeHyphenationPositions(
                concatText,
                hyphens,
                anyHyphensAuto
                    ? (hyphenator ?? EnUsHyphenation.Default)
                    : null,
                inlineTextPolicyPerRun,
                posToSrcRun,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Per-position soft-hyphen demotion. Under uniform mode this
        // applies when hyphens=None to the WHOLE concat. Under per-
        // run mode it applies to positions whose source run has
        // Hyphens=None.
        if (anyHyphensNone && hasSoftHyphen)
        {
            for (var i = 0; i < concatTotal; i++)
            {
                if (concatText[i] != '­') continue;
                if (breaks[i] != LineBreakOpportunity.Allowed) continue;
                var demote = inlineTextPolicyPerRun is null
                    ? hyphens == Hyphens.None
                    : (posToSrcRun is not null
                        && (uint)posToSrcRun[i] < (uint)inlineTextPolicyPerRun.Count
                        && inlineTextPolicyPerRun[posToSrcRun[i]].Hyphens == Hyphens.None);
                if (demote)
                {
                    breaks[i] = LineBreakOpportunity.Prohibited;
                }
            }
        }

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

                // Cycle 3b sub-cycle 2 — word-break:break-all upgrades
                // selected Prohibited boundaries to Allowed.
                //
                // Per PR #36 review fix (User #3 + #4): NOT every
                // Prohibited boundary becomes a candidate. Respect:
                //   1. Grapheme cluster boundaries (UAX #29 §3.1) —
                //      no break inside a cluster (combining marks,
                //      ZWJ-joined emoji sequences, regional-indicator
                //      flag pairs all stay atomic).
                //   2. Protected codepoint adjacencies — no break
                //      adjacent to ZWJ (U+200D), WJ (U+2060), NBSP
                //      (U+00A0), CM-attaching context. These have
                //      explicit non-break semantics in UAX #14
                //      (LB8a, LB11, LB12, LB12a) which BreakAll must
                //      not override.
                // Per Phase 3 Task 10 cycle 3d sub-cycle 3 — per-
                // source-run WordBreak. When inlineTextPolicyPerRun
                // is supplied, the BreakAll upgrade is per-glyph:
                // only glyphs whose source run has
                // <c>word-break: break-all</c> get their Prohibited
                // opportunities upgraded. Glyphs in Normal/KeepAll
                // source runs retain their UAX #14 classifications.
                //
                // Per sub-cycle 3 review Finding #3 — cross-run
                // BreakAll boundary uses the "either side may opt in"
                // rule (mirroring overflow-wrap's cross-run model
                // from sub-cycle 2). For the LAST glyph of a shaped
                // run that has a DIFFERENT next source run, if
                // either THIS run or the NEXT source run has
                // BreakAll, the boundary break is upgraded. This
                // lets a Normal prefix wrap immediately at the
                // boundary of a following BreakAll span (instead of
                // overflowing into the first BreakAll glyph before
                // the BreakAll's own upgrades take effect).
                var perGlyphBreakAll = breakAllGlyphs;
                if (inlineTextPolicyPerRun is not null)
                {
                    var srcRunIdxForBreakAll = shaped.Source.SourceTextRunIndex;
                    if ((uint)srcRunIdxForBreakAll < (uint)inlineTextPolicyPerRun.Count)
                    {
                        perGlyphBreakAll = inlineTextPolicyPerRun[srcRunIdxForBreakAll]
                            .WordBreak == WordBreak.BreakAll;
                    }
                    // Cross-run boundary upgrade (Finding #3).
                    if (!perGlyphBreakAll
                        && g + 1 == glyphs.Length
                        && r + 1 < shapedRuns.Count)
                    {
                        var nextSrcRunIdx = shapedRuns[r + 1].Source.SourceTextRunIndex;
                        if (nextSrcRunIdx != srcRunIdxForBreakAll
                            && (uint)nextSrcRunIdx < (uint)inlineTextPolicyPerRun.Count
                            && inlineTextPolicyPerRun[nextSrcRunIdx].WordBreak
                                == WordBreak.BreakAll)
                        {
                            perGlyphBreakAll = true;
                        }
                    }
                }
                if (perGlyphBreakAll && opp == LineBreakOpportunity.Prohibited)
                {
                    var clusterEndIdx = clusterEnd - 1;
                    var isGraphemeBreakHere = clusterEndIdx >= 0
                        && clusterEndIdx < concatTotal
                        && graphemeBreakAfter![clusterEndIdx];
                    if (isGraphemeBreakHere
                        && !IsBreakAllProtected(concatText, clusterEndIdx))
                    {
                        opp = LineBreakOpportunity.Allowed;
                    }
                }

                // Cycle 3b sub-cycle 3 — hyphenation candidates upgrade
                // Prohibited boundaries to Allowed at soft-hyphen
                // positions (Manual + Auto) + Liang-pattern positions
                // (Auto only). hyphenationAfter[clusterEnd-1] true =
                // hyphen-break opportunity AFTER this cluster.
                if (hyphenationAfter is not null
                    && opp == LineBreakOpportunity.Prohibited)
                {
                    var clusterEndIdx = clusterEnd - 1;
                    if ((uint)clusterEndIdx < (uint)hyphenationAfter.Length
                        && hyphenationAfter[clusterEndIdx])
                    {
                        opp = LineBreakOpportunity.Allowed;
                    }
                }

                // Per Phase 3 Task 10 cycle 3c — per-glyph WhiteSpace
                // honoring. When a per-source-run whiteSpacePerRun
                // array is supplied, EVERY glyph belonging to a
                // NoWrap or Pre source run gets its UAX #14 Allowed
                // opportunity downgraded to Prohibited (those modes
                // suppress soft wraps). This applies uniformly
                // across the whole NoWrap/Pre span — not just the
                // last glyph (per cycle 3c review Copilot #4
                // correction of an earlier comment that said "only
                // LAST glyph": the loop visits each glyph and
                // downgrades it independently, so all internal +
                // trailing Allowed positions inside the NoWrap span
                // are suppressed). Glyphs in surrounding wrap-
                // friendly runs (Normal / PreWrap / PreLine /
                // BreakSpaces) keep their Allowed candidates so the
                // wrap loop can snap to those boundaries when the
                // line overflows.
                //
                // Mixed-mode descendants like
                // `<span style="white-space:nowrap">` inside a
                // `white-space:normal` paragraph therefore wrap
                // ONLY at the surrounding Normal text's boundaries
                // (typically the SP between the prefix run and the
                // NoWrap span, or the SP after the NoWrap span ends).
                if (inlineTextPolicyPerRun is not null
                    && opp == LineBreakOpportunity.Allowed)
                {
                    var srcRunIdx = shaped.Source.SourceTextRunIndex;
                    if ((uint)srcRunIdx < (uint)inlineTextPolicyPerRun.Count)
                    {
                        var perRunWs = inlineTextPolicyPerRun[srcRunIdx].WhiteSpace;
                        if (perRunWs is WhiteSpace.Pre or WhiteSpace.NoWrap)
                        {
                            opp = LineBreakOpportunity.Prohibited;
                        }
                    }
                }

                // Tag mandatory-line-break control glyphs (LF, CR, VT,
                // FF, NEL, LS, PS). The painter must NOT emit glyph
                // data for these; the wrap loop trims them off the
                // drawable slice.
                var isMandatoryControl = false;
                // Cycle 3b sub-cycle 1 hardening — tag collapsible
                // break-space glyphs (SP / TAB after preprocessing
                // collapse). On soft-wrap snap-back the wrap loop
                // trims trailing IsBreakSpace glyphs from the drawable
                // slice + their advance, so the line's TotalAdvance
                // doesn't include the trailing collapsible whitespace
                // (per CSS Text L3 §4.1.2 "remove end-of-line spaces").
                //
                // Per Phase 3 Task 10 cycle 3d sub-cycle 1 review
                // Rec #1 — IsBreakSpace must be computed from the
                // SOURCE-RUN's WhiteSpace (not the global wrapWhiteSpace
                // override). When whiteSpacePerRun is supplied,
                // LayoutPerRun passes wrapWhiteSpace=Normal globally
                // for the per-glyph downgrade pipeline; that would
                // wrongly tag spaces in Pre/PreWrap/BreakSpaces source
                // runs as IsBreakSpace and trim them at soft-wrap
                // boundaries. Per CSS Text L3 §4.1, only collapse
                // modes (Normal/NoWrap/PreLine) produce trimmable
                // break-spaces; preserve modes keep their SPs visible.
                var isBreakSpace = false;
                // Cycle 3b sub-cycle 3 hardening — tag soft-hyphen
                // (U+00AD) glyphs. Per CSS Text L3 §6.1.1, soft-
                // hyphens are invisible unless the line breaks at
                // them (then the painter renders an explicit hyphen).
                // We zero their Advance for fit decisions + trim
                // them from drawable slices. Phase 4 painter sees
                // the soft-hyphen via the slice's source-glyph back-
                // reference but chooses NOT to render the .notdef
                // unless this is a hyphenation-break line.
                var isSoftHyphen = false;
                if ((uint)glyph.Cluster < (uint)concatTotal)
                {
                    var clusterChar = concatText[glyph.Cluster];
                    isMandatoryControl = IsMandatoryLineBreakControl(clusterChar);
                    // Per cycle 3d sub-cycle 1 Rec #1 — per-glyph
                    // IsBreakSpace. Refactored in sub-cycle 2 Rec #4
                    // to read from inlineTextPolicyPerRun's
                    // WhiteSpace field.
                    var glyphCollapses = inlineTextPolicyPerRun is not null
                        ? IsCollapseModeWhiteSpace(
                            inlineTextPolicyPerRun[shaped.Source.SourceTextRunIndex].WhiteSpace)
                        : collapsesSpaces;
                    if (glyphCollapses && (clusterChar == ' ' || clusterChar == '	'))
                    {
                        isBreakSpace = true;
                    }
                    if (clusterChar == '­')
                    {
                        isSoftHyphen = true;
                    }
                    // white-space: break-spaces (CSS Text L3 §6.4) — a line-break opportunity exists
                    // AFTER every preserved space and tab, INCLUDING between consecutive spaces (unlike
                    // pre-wrap, which only breaks at the UAX #14 Allowed positions). Upgrade each such
                    // preserved-space glyph's break-AFTER to Allowed. The per-glyph mode comes from the
                    // source run (LayoutPerRun passes the global `whiteSpace` as Normal), mirroring the
                    // `glyphCollapses` read above. Preserve-mode SPs are never trimmed (isBreakSpace stays
                    // false), so they keep their advance: break-spaces trailing spaces take up space and
                    // can wrap, they do NOT hang. Gated to BreakSpaces runs → other modes byte-identical.
                    var glyphWhiteSpace = inlineTextPolicyPerRun is not null
                        ? inlineTextPolicyPerRun[shaped.Source.SourceTextRunIndex].WhiteSpace
                        : whiteSpace;
                    if (glyphWhiteSpace == WhiteSpace.BreakSpaces
                        && (clusterChar == ' ' || clusterChar == '\t'))
                    {
                        opp = LineBreakOpportunity.Allowed;
                    }
                }

                // Zero advance for soft-hyphens — they're invisible
                // unless rendered at a break point (Phase 4 painter
                // handles the visible-hyphen-on-break case).
                var advanceForLayout = isSoftHyphen ? 0f : glyph.XAdvance;

                flat[flatIdx++] = new FlatGlyph(
                    RunIdx: r,
                    GlyphIdxInRun: g,
                    Advance: advanceForLayout,
                    Opportunity: opp,
                    IsMandatoryControl: isMandatoryControl,
                    IsBreakSpace: isBreakSpace,
                    IsSoftHyphen: isSoftHyphen);
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
                // Soft-wrap: snap back to lastAllowed. For collapsible
                // modes (Normal/NoWrap/PreLine) trim trailing
                // IsBreakSpace glyphs from the drawable slice — the
                // SP glyph at the break point is part of the source
                // text but should NOT contribute to the line's drawn
                // glyph stream or TotalAdvance per CSS Text L3 §4.1.2.
                // Cycle 3b sub-cycle 3 hardening: also trim trailing
                // IsSoftHyphen glyphs (invisible unless break point —
                // Phase 4 painter renders the visible hyphen).
                //
                // Detect "this snap is a hyphenation break" by
                // checking if the candidate IS a soft-hyphen or sits
                // at a hyphenation position recorded in
                // hyphenationAfter. The metadata propagates to the
                // emitted LineFragment for Phase 4's visible-hyphen
                // rendering.
                var drawableEnd = lastAllowed;
                if (collapsesSpaces)
                {
                    while (drawableEnd >= lineStart
                        && flat[drawableEnd].IsBreakSpace)
                    {
                        drawableEnd--;
                    }
                }
                // Always trim trailing soft-hyphens (even when
                // !collapsesSpaces) — Pre/PreWrap preserve regular
                // spaces but soft-hyphens stay invisible until
                // rendered at break per CSS Text L3 §6.1.1.
                while (drawableEnd >= lineStart
                    && flat[drawableEnd].IsSoftHyphen)
                {
                    drawableEnd--;
                }

                var endsWithHyphenation = false;
                if (hyphenationAfter is not null)
                {
                    // The candidate position lastAllowed corresponds to
                    // a glyph. Check if its cluster char is U+00AD OR
                    // the hyphenationAfter array marks the cluster
                    // end as a hyphenation position.
                    if (flat[lastAllowed].IsSoftHyphen)
                    {
                        endsWithHyphenation = true;
                    }
                    else
                    {
                        // For Liang positions: the lastAllowed glyph's
                        // cluster end is at hyphenationAfter[clusterEnd-1]
                        // = true iff this was a hyphenation candidate.
                        var lastShaped = shapedRuns[flat[lastAllowed].RunIdx];
                        var lastGlyph = lastShaped.Glyphs[flat[lastAllowed].GlyphIdxInRun];
                        var lastRunEnd = lastShaped.Source.Utf16Start + lastShaped.Source.Utf16Length;
                        var lastClusterEnd =
                            (flat[lastAllowed].GlyphIdxInRun + 1 < lastShaped.Glyphs.Length)
                                ? lastShaped.Glyphs[flat[lastAllowed].GlyphIdxInRun + 1].Cluster
                                : lastRunEnd;
                        var lastBreakIdx = lastClusterEnd - 1;
                        if ((uint)lastBreakIdx < (uint)hyphenationAfter.Length
                            && hyphenationAfter[lastBreakIdx])
                        {
                            endsWithHyphenation = true;
                        }
                    }
                }

                EmitDrawableRange(output, flat, lineStart, drawableEnd,
                    endsWithMandatoryBreak: false,
                    endsWithHyphenationBreak: endsWithHyphenation);
                lineStart = lastAllowed + 1;
                cursor = lineStart - 1; // for-loop increment lands on lineStart
                lineAdvance = 0;
                lastAllowed = -1;
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            // Cycle 3b sub-cycle 2 — overflow-wrap:anywhere fallback.
            // When the line would overflow + no UAX #14 Allowed
            // candidate exists in [lineStart, cursor), force a break.
            //
            // Per PR #36 review fix (User #2): Anywhere is GATED by
            // wrapsAtAllowed — under white-space:pre / white-space:nowrap
            // the wrap pass disallows all soft wraps; Anywhere must
            // honor that. Anywhere only fires when wrapsAtAllowed
            // is true.
            //
            // Per PR #36 review fix (Copilot #1): handle the
            // cursor == lineStart case explicitly — when a SINGLE
            // glyph is wider than the budget, emit it as its own
            // line (overflows by exactly one glyph) and advance
            // lineStart so the next iteration starts fresh. Without
            // this, the prior cursor>lineStart guard would let
            // additional glyphs accumulate.
            //
            // Per Phase 3 Task 10 cycle 3d sub-cycle 1 + 2 +
            // sub-cycle 2 review Recs #1 + #2 — gate the anywhere
            // forced-break fallback by per-glyph metadata. Per CSS
            // Text L3 §5.1, overflow-wrap "only has an effect when
            // white-space allows wrapping" + §5.2 keeps grapheme
            // clusters together. Three independent per-glyph gates
            // apply at the break point (between cursor-1 and cursor
            // when cursor > lineStart):
            //
            //   1. WhiteSpace gate (sub-cycle 1 + sub-cycle 2 Rec #2):
            //      Same source run on both sides → run's WhiteSpace
            //      must be wrap-friendly (not Pre/NoWrap).
            //      Cross source-run boundary → at least ONE side
            //      must be wrap-friendly. If both sides are
            //      Pre/NoWrap, the boundary is not a valid soft-
            //      wrap site even though it's a style boundary
            //      (per sub-cycle 2 review Rec #2).
            //
            //   2. OverflowWrap gate (sub-cycle 2): the cursor's
            //      source run must opt into overflow-wrap:anywhere
            //      for same-run breaks; for cross-run breaks at
            //      least one side must opt in.
            //
            //   3. Grapheme boundary gate (sub-cycle 2 Rec #1): per
            //      CSS Text L3, anywhere must keep grapheme clusters
            //      (UAX #29) together. Even when (1) + (2) pass, the
            //      forced break at cursor-1 must land on a grapheme
            //      boundary AND not be break-all-protected (ZWJ,
            //      WJ, NBSP adjacencies). When inside a multi-glyph
            //      cluster, suppress the fallback + let the cluster
            //      stay intact (as overflowing single emission once
            //      a boundary is reached).
            //
            // cursor == lineStart (single-glyph overflow): the lone
            // glyph emits alone (every glyph must go somewhere) —
            // existing best-effort behavior even for Pre/NoWrap or
            // non-anywhere runs.
            var anywhereAllowedHere = allowOverflowAnywhere;
            if (cursor > lineStart)
            {
                if (inlineTextPolicyPerRun is not null)
                {
                    var prevSrcRunIdx = shapedRuns[flat[cursor - 1].RunIdx]
                        .Source.SourceTextRunIndex;
                    var cursorSrcRunIdx = shapedRuns[flat[cursor].RunIdx]
                        .Source.SourceTextRunIndex;
                    var prevPolicy = inlineTextPolicyPerRun[prevSrcRunIdx];
                    var cursorPolicy = inlineTextPolicyPerRun[cursorSrcRunIdx];

                    // (1) WhiteSpace gate.
                    bool wsAllows;
                    if (prevSrcRunIdx == cursorSrcRunIdx)
                    {
                        wsAllows = IsWrapFriendlyWhiteSpace(cursorPolicy.WhiteSpace);
                    }
                    else
                    {
                        // Sub-cycle 2 Rec #2: cross-run requires at
                        // least one side wrap-friendly. Two adjacent
                        // non-wrap-friendly runs (Pre+NoWrap,
                        // NoWrap+Pre, Pre+Pre, NoWrap+NoWrap) cannot
                        // become a soft-wrap site just because they
                        // straddle a style boundary.
                        wsAllows = IsWrapFriendlyWhiteSpace(prevPolicy.WhiteSpace)
                            || IsWrapFriendlyWhiteSpace(cursorPolicy.WhiteSpace);
                    }

                    // (2) OverflowWrap gate.
                    // Per Phase 3 Task 12 sub-cycle 5 hardening
                    // Finding 5 — BreakWord behaves like Anywhere for
                    // line-wrap (both fire glyph-boundary fallback
                    // breaks); only intrinsic sizing distinguishes.
                    bool owAllows;
                    if (prevSrcRunIdx == cursorSrcRunIdx)
                    {
                        owAllows = cursorPolicy.OverflowWrap
                            is OverflowWrap.Anywhere or OverflowWrap.BreakWord;
                    }
                    else
                    {
                        owAllows = prevPolicy.OverflowWrap
                            is OverflowWrap.Anywhere or OverflowWrap.BreakWord
                            || cursorPolicy.OverflowWrap
                            is OverflowWrap.Anywhere or OverflowWrap.BreakWord;
                    }

                    anywhereAllowedHere = anywhereAllowedHere && wsAllows && owAllows;
                }

                // (3) Grapheme boundary gate (Rec #1) — even when
                // (1) + (2) pass, the break must land on a grapheme
                // cluster boundary. Apply uniformly whether per-run
                // arrays are supplied or not (combining marks +
                // ZWJ emoji + regional-indicator flags must stay
                // intact under uniform anywhere too).
                if (anywhereAllowedHere
                    && graphemeBreakAfter is not null
                    && allowOverflowAnywhere)
                {
                    if (!IsGraphemeBoundaryBetweenFlatGlyphs(
                            flat, cursor, shapedRuns, graphemeBreakAfter,
                            concatText, concatTotal))
                    {
                        anywhereAllowedHere = false;
                    }
                }
            }
            if (afterAdvance > availableInlineSize
                && anywhereAllowedHere
                && wrapsAtAllowed)
            {
                if (cursor > lineStart)
                {
                    // Force break at cursor-1: emit lineStart..cursor-1,
                    // start next line at cursor.
                    EmitDrawableRange(output, flat, lineStart, cursor - 1,
                        endsWithMandatoryBreak: false);
                    lineStart = cursor;
                    cursor = lineStart - 1;
                }
                else
                {
                    // Single glyph wider than line: emit just this
                    // glyph alone, advance.
                    EmitDrawableRange(output, flat, lineStart, cursor,
                        endsWithMandatoryBreak: false);
                    lineStart = cursor + 1;
                    // cursor stays — for-loop increment moves it past.
                }
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

    /// <summary>Per Phase 3 Task 9 cycle 3b sub-cycle 3 — compute
    /// hyphenation break positions. Returns a boolean array
    /// <c>hyphenationAfter[i]</c> = <see langword="true"/> when a
    /// hyphen-break is allowed BETWEEN code unit <c>i</c> and
    /// <c>i+1</c>.
    ///
    /// <para>Sources:</para>
    /// <list type="number">
    ///   <item>Soft-hyphens (U+00AD) in the source — every soft-
    ///   hyphen position becomes a break candidate when the source
    ///   run's Hyphens != None (Manual + Auto include them;
    ///   cycle 3d sub-cycle 4 gates per-source-run when the
    ///   <paramref name="inlineTextPolicyPerRun"/> + <paramref name="posToSrcRun"/>
    ///   args are supplied).</item>
    ///   <item>Liang-pattern auto-hyphenation — tokenize the text
    ///   into "words" (runs of letters), call the
    ///   <see cref="Hyphenator.FindHyphenationPoints"/> for each,
    ///   map word-relative positions back to concat-text positions.
    ///   Per cycle 3d sub-cycle 4, each word's Liang application
    ///   is gated by its FIRST letter's source-run Hyphens — apply
    ///   only when Hyphens=Auto.</item>
    /// </list>
    /// </summary>
    private static bool[] ComputeHyphenationPositions(
        string text, Hyphens hyphens, Hyphenator? autoHyphenator,
        IReadOnlyList<InlineTextPolicy>? inlineTextPolicyPerRun,
        int[]? posToSrcRun,
        CancellationToken cancellationToken)
    {
        var positions = new bool[text.Length];
        if (text.Length == 0) return positions;

        // Soft-hyphen pass. Per cycle 3d sub-cycle 4, gate per-
        // position on the source run's Hyphens — include if != None.
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '­') continue;
            var include = inlineTextPolicyPerRun is null
                ? hyphens != Hyphens.None
                : (posToSrcRun is not null
                    && (uint)posToSrcRun[i] < (uint)inlineTextPolicyPerRun.Count
                    && inlineTextPolicyPerRun[posToSrcRun[i]].Hyphens != Hyphens.None);
            if (include)
            {
                positions[i] = true;
            }
        }

        // Liang-pattern pass. Apply per-word when the word's first
        // letter's source-run Hyphens == Auto.
        if (autoHyphenator is not null)
        {
            ApplyLiangPatterns(text, autoHyphenator, positions,
                hyphens, inlineTextPolicyPerRun, posToSrcRun,
                cancellationToken);
        }

        return positions;
    }

    /// <summary>Maximum word length sent to the Liang hyphenator —
    /// per PR #37 review fix (User #4). Real natural-language words
    /// rarely exceed 32 chars; Long ASCII tokens (URLs, base64
    /// strings, code identifiers, hashes) can drive large
    /// allocations + CPU inside <see cref="Hyphenator.FindHyphenationPoints"/>
    /// and aren't meaningful hyphenation candidates anyway. Skip
    /// hyphenation entirely for words ≥ this length.</summary>
    private const int MaxLiangWordLength = 64;

    /// <summary>Walk the concat text, tokenize into word runs (ASCII
    /// letter sequences for cycle 3b sub-cycle 3 simplification),
    /// call the Hyphenator per word, and merge word-relative break
    /// positions into the concat-relative <paramref name="positions"/>
    /// array.
    ///
    /// <para>Cycle 3b sub-cycle 3 word definition: a maximal run of
    /// ASCII letters [A-Za-z]. Apostrophes inside contractions
    /// (don't, it's) are NOT treated as part of the word for
    /// hyphenation purposes — they break the word into segments.
    /// This is conservative; future cycles can integrate UAX #29
    /// word-segmentation for proper apostrophe handling.</para>
    ///
    /// <para>Per PR #37 review fix (User #4): cancellation is checked
    /// per-word, and words longer than <see cref="MaxLiangWordLength"/>
    /// are skipped to bound CPU + allocation under malicious or
    /// pathological input (long base64 strings, long identifiers,
    /// etc.).</para></summary>
    private static void ApplyLiangPatterns(
        string text, Hyphenator hyphenator, bool[] positions,
        Hyphens globalHyphens,
        IReadOnlyList<InlineTextPolicy>? inlineTextPolicyPerRun,
        int[]? posToSrcRun,
        CancellationToken cancellationToken)
    {
        var i = 0;
        while (i < text.Length)
        {
            // Skip non-(letter or soft-hyphen) chars. Per cycle 3d
            // sub-cycle 1 review Rec #6 — soft hyphens U+00AD are
            // included in the word-tokenization sweep so a word like
            // "rep­resent" is identified as ONE word (not two
            // segments split by the soft hyphen). The Liang call is
            // then SUPPRESSED for words containing U+00AD per CSS
            // Text L3 §6.1.1.
            while (i < text.Length && !IsAsciiLetterOrSoftHyphen(text[i])) i++;
            if (i >= text.Length) break;

            var start = i;
            while (i < text.Length && IsAsciiLetterOrSoftHyphen(text[i])) i++;
            var wordLen = i - start;
            if (wordLen < 2) continue; // tiny words — Hyphenator's leftMin filters anyway

            // Per PR #37 review fix (User #4): skip pathologically
            // long ASCII tokens — not real hyphenation candidates.
            if (wordLen > MaxLiangWordLength) continue;

            cancellationToken.ThrowIfCancellationRequested();

            // Per cycle 3d sub-cycle 4 — gate Liang application by
            // source-run Hyphens. Under uniform mode (no per-run
            // array) the global hyphens decides for the whole word.
            // Under per-run mode, the word may span multiple source
            // runs with different Hyphens values — so we run Liang
            // when ANY letter in the word is in a Hyphens=Auto run
            // (the per-position gate below filters out positions
            // landing inside non-Auto runs).
            //
            // Per sub-cycle 4 review Finding #1, the previous
            // first-letter-only gate was wrong: a word starting in
            // a non-Auto run would skip Liang entirely even if
            // later letters belonged to an Auto run (and vice
            // versa, an Auto-first word would apply Liang inside a
            // None or Manual segment).
            bool runLiang;
            if (inlineTextPolicyPerRun is null || posToSrcRun is null)
            {
                runLiang = globalHyphens == Hyphens.Auto;
            }
            else
            {
                runLiang = false;
                for (var k = 0; k < wordLen; k++)
                {
                    var srcIdx = posToSrcRun[start + k];
                    if ((uint)srcIdx < (uint)inlineTextPolicyPerRun.Count
                        && inlineTextPolicyPerRun[srcIdx].Hyphens == Hyphens.Auto)
                    {
                        runLiang = true;
                        break;
                    }
                }
            }
            if (!runLiang)
            {
                continue;
            }

            var wordSpan = text.AsSpan(start, wordLen);

            // Per Phase 3 Task 10 cycle 3d sub-cycle 1 review Rec #6
            // — CSS Text L3 §6.1.1: "When a soft hyphen is
            // encountered in the text, all such automatic
            // hyphenation opportunities elsewhere in that word
            // should be ignored." We skip Liang entirely when the
            // word contains a soft hyphen — the dedicated soft-
            // hyphen pass at <see cref="ComputeHyphenationPositions"/>
            // already recorded the U+00AD positions.
            //
            // Per sub-cycle 4 review Finding #2, the suppression
            // must only consider soft hyphens whose source run has
            // Hyphens != None. A disabled soft hyphen (Hyphens=None
            // in its source run) was demoted + excluded from
            // hyphenationAfter, so it shouldn't suppress valid Auto
            // Liang opportunities elsewhere in the same word.
            var hasActiveSoftHyphen = false;
            for (var k = 0; k < wordSpan.Length; k++)
            {
                if (wordSpan[k] != '­') continue;
                if (inlineTextPolicyPerRun is null || posToSrcRun is null)
                {
                    // Uniform mode — globalHyphens != None here
                    // (Liang was gated by Auto above, so SH is
                    // active).
                    hasActiveSoftHyphen = true;
                    break;
                }
                var shSrcIdx = posToSrcRun[start + k];
                if ((uint)shSrcIdx < (uint)inlineTextPolicyPerRun.Count
                    && inlineTextPolicyPerRun[shSrcIdx].Hyphens != Hyphens.None)
                {
                    hasActiveSoftHyphen = true;
                    break;
                }
            }
            if (hasActiveSoftHyphen) continue;

            var breaks = hyphenator.FindHyphenationPoints(wordSpan);
            // breaks[k] = position k in word means break BETWEEN
            // word[k-1] and word[k]. Concat position = start + k - 1
            // (the index of the LAST letter before the break).
            //
            // Per sub-cycle 4 review Finding #1, each break position
            // MUST be gated by the source run AT that position. A
            // word like Auto("hy") + None("phenation") should NOT
            // record a Liang break at the boundary or inside the
            // None segment.
            foreach (var k in breaks)
            {
                var concatIdx = start + k - 1;
                if ((uint)concatIdx >= (uint)positions.Length) continue;
                if (inlineTextPolicyPerRun is not null && posToSrcRun is not null)
                {
                    var srcIdx = posToSrcRun[concatIdx];
                    if ((uint)srcIdx >= (uint)inlineTextPolicyPerRun.Count
                        || inlineTextPolicyPerRun[srcIdx].Hyphens != Hyphens.Auto)
                    {
                        continue;
                    }
                }
                positions[concatIdx] = true;
            }
        }
    }

    /// <summary>ASCII letter test for cycle 3b sub-cycle 3's word
    /// tokenizer. Conservative — non-ASCII letters need UAX #29
    /// word-segmentation (deferred to later cycles).</summary>
    private static bool IsAsciiLetter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>Per Phase 3 Task 10 cycle 3d sub-cycle 1 review
    /// Rec #6 — extends <see cref="IsAsciiLetter"/> to ALSO accept
    /// soft hyphen U+00AD as a word-internal character so the
    /// tokenizer can identify words like "rep­resent" as ONE
    /// word (not two segments). Used by
    /// <see cref="ApplyLiangPatterns"/> to enforce CSS Text L3
    /// §6.1.1's "soft-hyphen suppresses Liang elsewhere in the
    /// word" rule.</summary>
    private static bool IsAsciiLetterOrSoftHyphen(char c) =>
        IsAsciiLetter(c) || c == '­';

    /// <summary>Per PR #36 review fix (cycle 3b sub-cycle 2 hardening) —
    /// returns <see langword="true"/> if a forced break BETWEEN
    /// position <paramref name="i"/> and <paramref name="i"/>+1 in
    /// <paramref name="text"/> is structurally protected (must not
    /// be upgraded to a candidate by word-break:break-all). Covers
    /// adjacencies to ZWJ (U+200D), WJ (U+2060), NBSP (U+00A0) which
    /// have explicit non-break semantics in UAX #14 (LB8a, LB11, LB12).
    /// Combining-mark + ligature attachment is already protected by
    /// the caller's grapheme-boundary check.</summary>
    private static bool IsBreakAllProtected(string text, int i)
    {
        if ((uint)i >= (uint)text.Length) return false;
        var here = text[i];
        if (IsBreakAllProtectedChar(here)) return true;
        if (i + 1 < text.Length && IsBreakAllProtectedChar(text[i + 1])) return true;
        return false;
    }

    /// <summary>Codepoints whose adjacency forbids breaking under
    /// word-break:break-all per UAX #14 protection rules.</summary>
    private static bool IsBreakAllProtectedChar(char c) =>
        c == '‍' // ZWJ — joins emoji + complex scripts atomically
        || c == '⁠' // WJ — explicit "do not break here"
        || c == ' '; // NBSP — non-breaking space

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
    ///   normalize segment breaks per §4.1.1 (CRLF → single LF;
    ///   lone CR → LF); whitespace otherwise preserved unchanged.</item>
    ///   <item><see cref="WhiteSpace.PreLine"/>: collapse SP+TAB runs
    ///   to a single SP; preserve LF segment breaks (CRLF / lone CR
    ///   normalized to LF per §4.1.1). Strips trailing SP at segment
    ///   ends per §4.1.2 "remove end-of-line spaces".</item>
    /// </list>
    ///
    /// <para><b>Single-run scope.</b> This overload preprocesses a
    /// SINGLE text string in isolation — leading + trailing
    /// whitespace are stripped under collapse modes assuming the
    /// input is the entire document content. For multi-run inline
    /// content where collapse state must carry across <c>TextRun</c>
    /// boundaries (e.g., <c>"Hello "</c> + styled <c>"world"</c>
    /// should collapse to <c>"Hello world"</c> with one space, not
    /// <c>"Helloworld"</c> if both runs strip independently), use
    /// <see cref="PreprocessTextRuns"/>.</para>
    ///
    /// <para><b>Cycle 3b sub-cycle 1 simplifications.</b> The
    /// "preserved tab" <c>tab-size</c> handling for Pre-mode is
    /// deferred (later sub-cycle). Cycle 3b ships the most common
    /// cases (Normal + NoWrap collapse, Pre/PreWrap preserve-with-
    /// segment-normalization, PreLine SP-collapse) which cover 99%
    /// of v1 invoice / report content.</para>
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
            WhiteSpace.Pre or WhiteSpace.PreWrap or WhiteSpace.BreakSpaces
                => NormalizeSegmentBreaks(text),
            WhiteSpace.PreLine => CollapseSpacesPreserveBreaks(text),
            WhiteSpace.Normal or WhiteSpace.NoWrap => CollapseAllWhitespace(text),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "PreprocessWhitespace: mode must be a defined WhiteSpace value."),
        };
    }

    /// <summary>White-space:normal / nowrap — collapse all SP/TAB/LF/
    /// CR/FF runs to a single SP + strip leading/trailing SP. Fast-
    /// path: returns input unchanged when there's no whitespace at
    /// all OR whitespace appears only as single SPs between non-WS
    /// chars + no leading/trailing whitespace (Copilot PR #35 review).</summary>
    private static string CollapseAllWhitespace(string text)
    {
        if (text.Length == 0) return text;
        if (!NeedsCollapseAllWhitespace(text)) return text;

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
    /// single SP, preserve LF segment breaks (CRLF / lone CR
    /// normalized to LF per §4.1.1), strip trailing SP at segment
    /// ends per §4.1.2. Fast-path: returns input unchanged when
    /// already in canonical form.</summary>
    private static string CollapseSpacesPreserveBreaks(string text)
    {
        if (text.Length == 0) return text;
        if (!NeedsCollapseSpacesPreserveBreaks(text)) return text;

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
            else if (c == '\u000A')
            {
                // LF segment break — strip pending trailing SP.
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                {
                    sb.Length--;
                }
                sb.Append('\u000A');
                inSpaceRun = true;
            }
            else if (c == '\u000D')
            {
                // CR — normalize CRLF / lone CR to LF per §4.1.1.
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                {
                    sb.Length--;
                }
                sb.Append('\u000A');
                inSpaceRun = true;
                if (i + 1 < text.Length && text[i + 1] == '\u000A')
                {
                    i++; // consume LF — CRLF collapses to single LF
                }
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

    /// <summary>Returns <see langword="false"/> when input is already
    /// in canonical-collapsed form for Normal/NoWrap (no leading/
    /// trailing SP, no consecutive WS, no non-SP whitespace) — caller
    /// can return the input unchanged. Per Copilot PR #35 fast-path
    /// recommendation.</summary>
    private static bool NeedsCollapseAllWhitespace(string text)
    {
        if (text.Length == 0) return false;
        if (IsCssWhiteSpace(text[0])) return true;
        if (IsCssWhiteSpace(text[text.Length - 1])) return true;
        var prevWs = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsCssWhiteSpace(c))
            {
                if (c != ' ') return true; // non-SP WS needs convert
                if (prevWs) return true; // consecutive WS run
                prevWs = true;
            }
            else
            {
                prevWs = false;
            }
        }
        return false;
    }

    /// <summary>Returns <see langword="false"/> when input is already
    /// in canonical PreLine form (only LF segment breaks; no CR; no
    /// TAB/FF; no SP runs; no leading/trailing SP within segments).</summary>
    private static bool NeedsCollapseSpacesPreserveBreaks(string text)
    {
        if (text.Length == 0) return false;
        var c0 = text[0];
        if (c0 == ' ' || c0 == '\u0009' || c0 == '\u000C') return true;
        var cn = text[text.Length - 1];
        if (cn == ' ' || cn == '\u0009' || cn == '\u000C') return true;
        var prevSp = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\u0009' || c == '\u000C' || c == '\u000D') return true;
            if (c == ' ')
            {
                if (prevSp) return true;
                prevSp = true;
            }
            else if (c == '\u000A')
            {
                if (prevSp) return true; // trailing SP before LF
                if (i + 1 < text.Length)
                {
                    var nx = text[i + 1];
                    if (nx == ' ' || nx == '\u0009' || nx == '\u000C') return true;
                }
                prevSp = false;
            }
            else
            {
                prevSp = false;
            }
        }
        return false;
    }

    /// <summary>Per Phase 3 Task 9 cycle 3b sub-cycle 1 hardening —
    /// inline-context white-space preprocessor. Carries collapse
    /// state across <see cref="TextRun"/> boundaries so a trailing
    /// SP in run N collapses with a leading SP in run N+1 to a
    /// single SP, not two SPs (or a missing SP if both runs strip
    /// independently).
    ///
    /// <para><b>Why a separate API?</b>
    /// <see cref="PreprocessWhitespace(string, WhiteSpace)"/> treats
    /// its single string as a complete document — it strips both
    /// leading + trailing whitespace. For multi-run inline content
    /// (e.g., a paragraph with styled <c>&lt;em&gt;</c> children)
    /// each run is a fragment; only the document-leading SP of run 0
    /// + document-trailing SP of the last run should strip. Internal
    /// run boundaries preserve the collapse state.</para>
    ///
    /// <para><b>Algorithm.</b> Walks each run in order with a shared
    /// <c>inWs</c> state (initialized to <see langword="true"/> to
    /// strip document-leading whitespace). After all runs are
    /// processed, the final trailing SP is stripped from the last
    /// run's output.</para>
    ///
    /// <para><b>Per-run mode (cycle 3b sub-cycle 1 simplification).</b>
    /// Cycle 3b sub-cycle 1 applies one <see cref="WhiteSpace"/> mode
    /// to ALL runs — adequate for paragraphs whose every descendant
    /// inherits the same <c>white-space</c> property. Per-run mode
    /// (mixed inline descendants) is scheduled for Task 10's
    /// <c>InlineLayouter</c> integration.</para>
    /// </summary>
    /// <param name="runs">The source runs in document order.</param>
    /// <param name="mode">The CSS <c>white-space</c> value to apply
    /// uniformly across all runs.</param>
    /// <returns>A new <see cref="TextRun"/> array with each run's
    /// text preprocessed + collapse state carried across boundaries.
    /// Empty input returns empty.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="runs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="mode"/> is not a defined
    /// <see cref="WhiteSpace"/> value.</exception>
    public static IReadOnlyList<TextRun> PreprocessTextRuns(
        IReadOnlyList<TextRun> runs, WhiteSpace mode)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0) return runs;

        if (mode is WhiteSpace.Pre or WhiteSpace.PreWrap or WhiteSpace.BreakSpaces)
        {
            var preNormalized = new TextRun[runs.Count];
            for (var i = 0; i < runs.Count; i++)
            {
                var raw = runs[i].Text;
                var normalized = NormalizeSegmentBreaks(raw);
                preNormalized[i] = ReferenceEquals(raw, normalized)
                    ? runs[i]
                    : new TextRun(normalized, runs[i].Style);
            }
            return preNormalized;
        }

        if (mode is not (WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode,
                "PreprocessTextRuns: mode must be a defined WhiteSpace value.");
        }

        var preserveBreaks = mode == WhiteSpace.PreLine;
        var output = new TextRun[runs.Count];
        var inWs = true;
        for (var r = 0; r < runs.Count; r++)
        {
            // Inline-atomic-boxes cycle — an atomic run (its text is a single U+FFFC) is an opaque
            // non-whitespace unit: pass it through VERBATIM (preserving the Atomic payload that a
            // `new TextRun(text, style)` would drop) and reset the collapse state so a following space
            // isn't trimmed as leading.
            if (runs[r].Atomic is not null)
            {
                output[r] = runs[r];
                inWs = false;
                continue;
            }
            output[r] = new TextRun(
                CollapseStateful(runs[r].Text, preserveBreaks, ref inWs),
                runs[r].Style);
        }

        if (output.Length > 0)
        {
            var last = output.Length - 1;
            var t = output[last].Text;
            if (t.Length > 0 && t[t.Length - 1] == ' ')
            {
                output[last] = new TextRun(t.Substring(0, t.Length - 1),
                    output[last].Style);
            }
        }

        return output;
    }

    /// <summary>Per Phase 3 Task 10 cycle 3d sub-cycle 1 — per-source-
    /// run white-space preprocessor. Each run is processed with its
    /// OWN <see cref="WhiteSpace"/> mode, enabling mixed inline
    /// descendants like <c>&lt;pre&gt;</c> inside <c>white-space:
    /// normal</c> text to preserve their content while the
    /// surrounding text continues to collapse per CSS Text L3 §4.1.
    /// Unlike the uniform-mode <see cref="PreprocessTextRuns"/>,
    /// this method dispatches per run.
    ///
    /// <para><b>Cross-run state semantics.</b>
    /// <list type="bullet">
    ///   <item>Two consecutive <b>collapse</b>-mode runs
    ///   (<see cref="WhiteSpace.Normal"/>, <see cref="WhiteSpace.NoWrap"/>,
    ///   <see cref="WhiteSpace.PreLine"/>) chain their <c>inWs</c>
    ///   state across the boundary, so a trailing SP in run N + a
    ///   leading SP in run N+1 collapse to a single SP.</item>
    ///   <item>A <b>preserve</b>-mode run
    ///   (<see cref="WhiteSpace.Pre"/>, <see cref="WhiteSpace.PreWrap"/>,
    ///   <see cref="WhiteSpace.BreakSpaces"/>) emits its text
    ///   character-for-character (after CR/LF normalization). It
    ///   resets <c>inWs</c> to <see langword="false"/> for the next
    ///   collapse run — preserve content sits as-is in the concat,
    ///   not interacting with collapse decisions on either side.</item>
    ///   <item>The document-leading SP strip applies only when the
    ///   FIRST run is a collapse mode (initial <c>inWs = true</c>).
    ///   The document-trailing SP strip applies only when the LAST
    ///   run is a collapse mode.</item>
    /// </list></para>
    ///
    /// <para><b>Spec note.</b> CSS Text L3 §4.1 models white-space
    /// per-element, but the cross-element interaction at run
    /// boundaries (collapse-run + preserve-run + collapse-run) is an
    /// interop area not tightly specified. This implementation
    /// follows the "preserve runs are atomic; collapse runs chain via
    /// <c>inWs</c>" rule, which matches the most common UA behavior
    /// for the invoice/report content NetPdf targets.</para>
    /// </summary>
    /// <param name="runs">The source runs in document order.</param>
    /// <param name="modes">Per-source-run <see cref="WhiteSpace"/>
    /// modes. MUST have the same length as <paramref name="runs"/>.
    /// Each entry MUST be a defined <see cref="WhiteSpace"/> value.</param>
    /// <param name="cancellationToken">Per Phase 3 Task 10 cycle 3d
    /// sub-cycle 1 review Rec #4 — cooperative cancellation. Checked
    /// at method entry + once per source-run boundary so large
    /// hostile inline text doesn't waste CPU after the caller signals
    /// cancellation. The character-level loops inside
    /// <see cref="CollapseStateful"/> + <see cref="NormalizeSegmentBreaks"/>
    /// are not currently broken into chunks (the granularity is
    /// per-source-run); this is adequate when source runs are
    /// typical-paragraph-sized.</param>
    /// <returns>A new <see cref="TextRun"/> array with each run's
    /// text preprocessed using its individual mode + cross-run state
    /// managed per the rules above. Empty input returns empty.</returns>
    /// <exception cref="ArgumentNullException">A required arg is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="modes"/>'s
    /// length doesn't match <paramref name="runs"/>'s, or a mode
    /// entry is not a defined <see cref="WhiteSpace"/> value.</exception>
    /// <exception cref="System.OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.</exception>
    public static IReadOnlyList<TextRun> PreprocessTextRunsPerRun(
        IReadOnlyList<TextRun> runs, IReadOnlyList<WhiteSpace> modes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(modes);
        if (runs.Count != modes.Count)
        {
            throw new ArgumentException(
                $"PreprocessTextRunsPerRun: modes length ({modes.Count}) " +
                $"must match runs count ({runs.Count}).",
                nameof(modes));
        }
        // Validate every mode entry is a defined WhiteSpace value.
        for (var i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            if (m is not (WhiteSpace.Normal
                or WhiteSpace.Pre
                or WhiteSpace.NoWrap
                or WhiteSpace.PreWrap
                or WhiteSpace.PreLine
                or WhiteSpace.BreakSpaces))
            {
                throw new ArgumentException(
                    $"PreprocessTextRunsPerRun: modes[{i}] = {m} is not " +
                    $"a defined WhiteSpace value.",
                    nameof(modes));
            }
        }
        if (runs.Count == 0) return runs;

        // Per cycle 3d sub-cycle 1 review Rec #4 — pre-cancelled
        // tokens fast-path out before any allocation.
        cancellationToken.ThrowIfCancellationRequested();

        var output = new TextRun[runs.Count];
        // Initial `inWs = true` strips document-leading whitespace
        // ONLY if the first run is a collapse mode. For a preserve-
        // first document the preserve run emits its content as-is,
        // so the strip never fires.
        var inWs = true;

        for (var r = 0; r < runs.Count; r++)
        {
            // Per cycle 3d sub-cycle 1 review Rec #4 — observe
            // cancellation at every source-run boundary.
            cancellationToken.ThrowIfCancellationRequested();
            // Inline-atomic-boxes cycle — an atomic run (its text is a single U+FFFC) is an opaque
            // non-whitespace unit: pass it through VERBATIM (preserving the Atomic payload) regardless
            // of its mode, and reset the collapse state so a following space isn't trimmed as leading.
            if (runs[r].Atomic is not null)
            {
                output[r] = runs[r];
                inWs = false;
                continue;
            }
            var mode = modes[r];
            if (mode is WhiteSpace.Pre
                or WhiteSpace.PreWrap
                or WhiteSpace.BreakSpaces)
            {
                // Preserve mode — normalize CR/LF + emit as-is.
                var raw = runs[r].Text;
                var normalized = NormalizeSegmentBreaks(raw);
                output[r] = ReferenceEquals(raw, normalized)
                    ? runs[r]
                    : new TextRun(normalized, runs[r].Style);
                // Reset inWs for the next run — preserve content
                // doesn't interact with collapse decisions either side.
                inWs = false;
            }
            else
            {
                // Collapse mode (Normal / NoWrap / PreLine).
                var preserveBreaks = mode == WhiteSpace.PreLine;
                output[r] = new TextRun(
                    CollapseStateful(runs[r].Text, preserveBreaks, ref inWs),
                    runs[r].Style);
            }
        }

        // Document-trailing SP strip — only when the LAST run is a
        // collapse mode. For preserve-last documents, the trailing
        // whitespace is part of the preserved content.
        var lastIdx = output.Length - 1;
        var lastMode = modes[lastIdx];
        if (lastMode is WhiteSpace.Normal
            or WhiteSpace.NoWrap
            or WhiteSpace.PreLine)
        {
            var t = output[lastIdx].Text;
            if (t.Length > 0 && t[t.Length - 1] == ' ')
            {
                output[lastIdx] = new TextRun(
                    t.Substring(0, t.Length - 1),
                    output[lastIdx].Style);
            }
        }

        return output;
    }

    /// <summary>Stateful collapse helper for
    /// <see cref="PreprocessTextRuns"/>. Carries the <c>inWs</c>
    /// state across calls so consecutive runs collapse their
    /// boundary whitespace correctly. Does NOT strip trailing SP
    /// (caller handles document-trailing strip after the last run).</summary>
    private static string CollapseStateful(string text, bool preserveBreaks, ref bool inWs)
    {
        if (text.Length == 0) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (preserveBreaks && c == '\u000A')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                sb.Append('\u000A');
                inWs = true;
            }
            else if (preserveBreaks && c == '\u000D')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                sb.Append('\u000A');
                inWs = true;
                if (i + 1 < text.Length && text[i + 1] == '\u000A') i++;
            }
            else if (preserveBreaks
                ? (c == ' ' || c == '\u0009' || c == '\u000C')
                : IsCssWhiteSpace(c))
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
        return sb.ToString();
    }

    /// <summary>Apply CSS Text L3 §4.1.1 segment-break-transformation:
    /// any CR followed by LF → single LF; remaining lone CR → LF.
    /// Fast-path: returns input unchanged when there are no CRs.</summary>
    private static string NormalizeSegmentBreaks(string text)
    {
        if (text.Length == 0) return text;
        if (text.IndexOf('\u000D') < 0) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\u000D')
            {
                sb.Append('\u000A');
                if (i + 1 < text.Length && text[i + 1] == '\u000A')
                {
                    i++;
                }
            }
            else
            {
                sb.Append(c);
            }
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

    /// <summary>Per Phase 3 Task 10 cycle 3d sub-cycle 1 review
    /// Rec #1 — predicate identifying collapse-mode <see cref="WhiteSpace"/>
    /// values. Per CSS Text L3 §4.1, only Normal / NoWrap / PreLine
    /// collapse runs of whitespace; preserve modes
    /// (Pre / PreWrap / BreakSpaces) keep all whitespace
    /// character-for-character. Used by the wrap pass to decide
    /// whether a SP/TAB glyph is trimmable from a line's drawable
    /// slice (collapse-mode → trimmable; preserve-mode → must
    /// remain in output).</summary>
    private static bool IsCollapseModeWhiteSpace(WhiteSpace ws) =>
        ws is WhiteSpace.Normal
            or WhiteSpace.NoWrap
            or WhiteSpace.PreLine;

    /// <summary>Per Phase 3 Task 10 cycle 3d sub-cycle 1 review
    /// Rec #2 — predicate identifying wrap-friendly
    /// <see cref="WhiteSpace"/> values. Per CSS Text L3 §3,
    /// Pre / NoWrap suppress wrapping at UAX #14 Allowed
    /// opportunities; the other 4 modes wrap. Per §5.1, the
    /// <c>overflow-wrap</c> property has effect ONLY where
    /// wrapping is otherwise allowed — so the
    /// <c>overflow-wrap: anywhere</c> forced-break fallback also
    /// must not fire inside Pre / NoWrap source-run spans.</summary>
    private static bool IsWrapFriendlyWhiteSpace(WhiteSpace ws) =>
        ws is not (WhiteSpace.Pre or WhiteSpace.NoWrap);

    /// <summary>Per Phase 3 Task 10 cycle 3d sub-cycle 2 review
    /// Rec #1 — checks whether the boundary BETWEEN <c>flat[cursor-1]</c>
    /// and <c>flat[cursor]</c> lands on a grapheme cluster boundary
    /// (UAX #29 §3.1) AND is not adjacency-protected (LB8a/LB11/
    /// LB12 — ZWJ, WJ, NBSP). The forced overflow-wrap:anywhere
    /// break must NOT split inside a multi-glyph cluster
    /// (combining marks, ZWJ-joined emoji, regional-indicator flag
    /// pairs).
    ///
    /// <para>Returns <see langword="true"/> when the break point
    /// is safe to use, <see langword="false"/> when the boundary is
    /// inside an atomic cluster + the fallback must skip this
    /// position (the wrap loop continues + retries at the next
    /// cursor, which sits on a glyph that DOES start a new
    /// cluster).</para></summary>
    private static bool IsGraphemeBoundaryBetweenFlatGlyphs(
        FlatGlyph[] flat, int cursor,
        IReadOnlyList<ShapedRun> shapedRuns,
        bool[] graphemeBreakAfter,
        string concatText, int concatTotal)
    {
        // Compute the UTF-16 cluster-end position of flat[cursor-1].
        // For LTR runs the next-glyph's cluster is the cluster-end
        // (matches the existing flat-build logic). For RTL runs
        // (cycle 3a fallback) we approximate as cluster + 1 which
        // is correct for single-codeunit clusters.
        var prev = flat[cursor - 1];
        var prevShaped = shapedRuns[prev.RunIdx];
        var prevGlyphs = prevShaped.Glyphs;
        var isRtl = (prevShaped.Source.BidiLevel & 1) != 0;
        int clusterEnd;
        if (!isRtl)
        {
            clusterEnd = (prev.GlyphIdxInRun + 1 < prevGlyphs.Length)
                ? prevGlyphs[prev.GlyphIdxInRun + 1].Cluster
                : prevShaped.Source.Utf16Start + prevShaped.Source.Utf16Length;
        }
        else
        {
            clusterEnd = prevGlyphs[prev.GlyphIdxInRun].Cluster + 1;
        }
        // Multi-glyph SINGLE-cluster shapes (combining marks
        // attached to a base char; ligatures; ZWJ-joined sequences)
        // yield clusterEnd == prev's own cluster (HarfBuzz keeps
        // glyphs in the same source cluster). That's never a valid
        // grapheme boundary — the cluster spans multiple glyphs.
        var prevCluster = prevGlyphs[prev.GlyphIdxInRun].Cluster;
        if (clusterEnd <= prevCluster)
        {
            return false;
        }
        var graphemeIdx = clusterEnd - 1;
        if ((uint)graphemeIdx >= (uint)concatTotal)
        {
            // End-of-input — always a valid boundary.
            return true;
        }
        if (!graphemeBreakAfter[graphemeIdx])
        {
            // Inside a multi-codepoint grapheme cluster.
            return false;
        }
        // Adjacency-protected positions (ZWJ, WJ, NBSP) are not
        // valid anywhere break points even when grapheme-boundary
        // analysis would allow them.
        if (IsBreakAllProtected(concatText, graphemeIdx))
        {
            return false;
        }
        return true;
    }

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
        bool endsWithMandatoryBreak,
        bool endsWithHyphenationBreak = false)
    {
        if (start > end)
        {
            // Empty drawable range (a control-only line, e.g., lone LF).
            output.Add(new LineFragment(
                Slices: Array.Empty<ShapedRunSlice>(),
                TotalAdvance: 0,
                EndsWithMandatoryBreak: endsWithMandatoryBreak,
                EndsWithHyphenationBreak: endsWithHyphenationBreak));
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
            EndsWithMandatoryBreak: endsWithMandatoryBreak,
            EndsWithHyphenationBreak: endsWithHyphenationBreak));
    }

    /// <summary>Cycle 3a/3b internal: a flattened glyph view across
    /// all shaped runs, indexed by global glyph position.
    /// <see cref="IsMandatoryControl"/> is set for glyphs whose
    /// source codepoint is a UAX #14 hard-line-break control (LF,
    /// CR, VT, FF, NEL, LS, PS) — these are trimmed off the end of
    /// each emitted drawable slice. <see cref="IsBreakSpace"/> is set
    /// (cycle 3b sub-cycle 1) for collapsible-whitespace glyphs
    /// (SP/TAB) under collapsible white-space modes (Normal/NoWrap/
    /// PreLine) — these are trimmed off soft-wrap snap-back drawable
    /// slices so the line's TotalAdvance doesn't include the trailing
    /// SP per CSS Text L3 §4.1.2. <see cref="IsSoftHyphen"/> is set
    /// (cycle 3b sub-cycle 3 hardening) for soft-hyphen U+00AD
    /// glyphs — invisible unless the line breaks at the soft-hyphen,
    /// in which case the painter (Phase 4) renders an explicit
    /// hyphen glyph. The line builder zeroes their
    /// <see cref="Advance"/> for fit decisions + trims them off
    /// drawable slices.</summary>
    private readonly record struct FlatGlyph(
        int RunIdx,
        int GlyphIdxInRun,
        float Advance,
        LineBreakOpportunity Opportunity,
        bool IsMandatoryControl,
        bool IsBreakSpace,
        bool IsSoftHyphen);
}
