// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Inline;

/// <summary>
/// Per Phase 3 Task 9 cycle 3a — one wrapped line of inline content,
/// ready for the painter to position. A <see cref="LineFragment"/>
/// is the output of <see cref="LineBuilder.Wrap"/>; the integrating
/// <c>InlineLayouter</c> (Task 10) places fragments at successive
/// baselines until the block's content area is filled.
///
/// <para><b>Composition.</b> A line is a sequence of
/// <see cref="ShapedRunSlice"/>s — one slice per shaped run that
/// contributes to the line. A single shaped run may span multiple
/// lines (split by the wrapping pass) so the per-slice
/// <see cref="ShapedRunSlice.GlyphStart"/> +
/// <see cref="ShapedRunSlice.GlyphLength"/> identify the exact glyph
/// subrange on this line.</para>
///
/// <para><b>Cycle 3a scope.</b> Cycle 3a wraps with a naive greedy
/// algorithm — fill the line up to <c>availableInlineSize</c>, snap
/// back to the most recent <c>Allowed</c> break opportunity (UAX #14),
/// emit a fragment, repeat. <c>Mandatory</c> breaks force a
/// fragment boundary even mid-line; mandatory-control glyphs
/// (LF/CR/NEL/VT/FF/LS/PS) are trimmed off the drawable slice so
/// the painter never emits glyph data for them. CRLF strips both
/// CR + LF on the same line. No hyphenation, no
/// <c>overflow-wrap</c>/<c>word-break</c> overrides, no
/// <c>text-align</c>/<c>vertical-align</c> processing — all deferred
/// to cycle 3b/c.</para>
///
/// <para><b>Cycle 3b white-space pipeline.</b> CSS <c>white-space</c>
/// processing is split between TWO call sites:
/// <list type="number">
///   <item><b>Pre-shaping preprocessing</b> via
///   <see cref="LineBuilder.PreprocessWhitespace"/> or
///   <see cref="LineBuilder.PreprocessTextRuns"/> — applies the
///   collapse / preserve / segment-break-normalization rules from
///   CSS Text L3 §4.1 to the source text BEFORE
///   <see cref="LineBuilder.Itemize"/> + <see cref="LineBuilder.Shape"/>
///   are called. Wrap can't collapse post-hoc because shaping
///   already turned the text into glyphs with concat-relative
///   cluster offsets.</item>
///   <item><b>Wrap-time mode honoring</b> via the
///   <see cref="WhiteSpace"/> argument on
///   <see cref="LineBuilder.Wrap"/>. Cycle 3b sub-cycle 1 honors
///   <see cref="WhiteSpace.Pre"/> + <see cref="WhiteSpace.NoWrap"/>
///   to suppress UAX #14 Allowed-break wrapping (Mandatory still
///   honored), and trims trailing collapsible-whitespace glyphs off
///   the drawable slice when wrapping at an Allowed break in
///   collapsible modes (Normal/NoWrap/PreLine) per §4.1.2.</item>
/// </list>
/// </para>
///
/// <para><b>Cycle 3a deferrals (subsequent cycles):</b></para>
/// <list type="bullet">
///   <item>White-space: <c>pre</c>/<c>pre-wrap</c>/<c>pre-line</c>/
///   <c>nowrap</c> variants (cycle 3b).</item>
///   <item><c>overflow-wrap: anywhere</c> + <c>word-break: break-all</c>
///   (cycle 3b).</item>
///   <item>Hyphenation via Liang patterns (cycle 3b — primitives
///   already exist in <c>NetPdf.Text.Hyphenation</c>).</item>
///   <item><c>text-align</c> (start/end/center/justify) — cycle 3a
///   emits left-aligned fragments only (cycle 3c).</item>
///   <item><c>vertical-align</c> baseline shifts (cycle 3c).</item>
///   <item>RTL line reversal at the fragment level (cycle 3c —
///   cycle 2 already produces RTL glyph arrays in HarfBuzz visual
///   order; cycle 3c reverses fragment-level slice order for RTL
///   paragraphs).</item>
/// </list>
/// </summary>
/// <param name="Slices">The shaped-run slices making up this line in
/// document order. Empty array represents an empty line (e.g., a
/// mandatory break after another mandatory break).</param>
/// <param name="TotalAdvance">Sum of <see cref="ShapedRunSlice.SliceAdvance"/>
/// across <see cref="Slices"/>, in CSS px. Cycle 3c reads this for
/// <c>text-align</c> and justification.</param>
/// <param name="EndsWithMandatoryBreak"><see langword="true"/> when
/// this line was terminated by a UAX #14 <c>Mandatory</c> break
/// opportunity (LF, CR, NEL, ¶, etc.). <see langword="false"/> when
/// terminated by a soft wrap or end of input. The painter uses this
/// to decide whether a trailing CRLF should also reset the
/// horizontal cursor (mandatory) or just advance the baseline (soft
/// wrap).</param>
internal readonly record struct LineFragment(
    ShapedRunSlice[] Slices,
    double TotalAdvance,
    bool EndsWithMandatoryBreak);
