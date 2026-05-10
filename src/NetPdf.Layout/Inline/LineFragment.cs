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
/// <para><b>Wrap algorithm.</b> Greedy fill — accumulate glyphs up
/// to <c>availableInlineSize</c>, snap back to the most recent
/// <c>Allowed</c> break opportunity (UAX #14), emit a fragment,
/// repeat. <c>Mandatory</c> breaks force a fragment boundary even
/// mid-line; mandatory-control glyphs (LF/CR/NEL/VT/FF/LS/PS) are
/// trimmed off the drawable slice so the painter never emits glyph
/// data for them. CRLF strips both CR + LF on the same line.</para>
///
/// <para><b>White-space pipeline (cycle 3b + 3c).</b> CSS
/// <c>white-space</c> processing is split between TWO call sites:
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
///   <item><b>Per-source-run honoring</b> via the optional
///   <c>whiteSpacePerRun</c> parameter on
///   <see cref="LineBuilder.Wrap"/> (cycle 3c). When supplied,
///   each glyph's UAX #14 Allowed opportunity is downgraded to
///   Prohibited if its source TextRun's WhiteSpace ∈
///   {<see cref="WhiteSpace.NoWrap"/>, <see cref="WhiteSpace.Pre"/>}.
///   The <see cref="InlineLayouter.LayoutPerRun"/> facade builds
///   this array automatically for mixed Normal/NoWrap inputs
///   (both share collapse semantics per §4.1). Pre/PreWrap/
///   PreLine/BreakSpaces mixes require per-source-run preprocessing
///   + still throw at the facade pending cycle 3d.</item>
/// </list>
/// </para>
///
/// <para><b>Shipped capabilities (3a / 3b / 3c):</b>
/// <list type="bullet">
///   <item>Cycle 3a — greedy wrap at UAX #14 Allowed boundaries,
///   Mandatory break handling, mandatory-control glyph trim, multi-
///   shaped-run slices, RTL glyph passthrough in HarfBuzz visual
///   order.</item>
///   <item>Cycle 3b — all 6 CSS white-space modes (<c>normal</c>,
///   <c>pre</c>, <c>nowrap</c>, <c>pre-wrap</c>, <c>pre-line</c>,
///   <c>break-spaces</c> — last folds to <c>pre-wrap</c>);
///   <c>overflow-wrap: anywhere</c> + <c>word-break: break-all</c>
///   forced-break fallback with grapheme-cluster + protected-
///   codepoint guards; Liang-pattern hyphenation under
///   <c>hyphens: auto</c> + soft-hyphen handling.</item>
///   <item>Cycle 3c — per-source-run <c>WhiteSpace</c> array
///   (Normal/NoWrap matrix) for mixed inline descendants.</item>
/// </list>
/// </para>
///
/// <para><b>Subsequent-cycle deferrals:</b>
/// <list type="bullet">
///   <item>Per-source-run preprocessing for Pre/PreWrap/PreLine/
///   BreakSpaces mixed with Normal/NoWrap (cycle 3d).</item>
///   <item>Per-glyph overflow-wrap / word-break / hyphens for
///   mixed-mode descendants (cycle 3d / 3e).</item>
///   <item><c>text-align</c> (start/end/center/justify) — wrap
///   currently emits left-aligned fragments only.</item>
///   <item><c>vertical-align</c> baseline shifts.</item>
///   <item>RTL line reversal at the fragment level — cycle 2
///   already produces RTL glyph arrays in HarfBuzz visual order;
///   a subsequent cycle reverses fragment-level slice order for
///   RTL paragraphs.</item>
/// </list>
/// </para>
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
/// <param name="EndsWithHyphenationBreak">Per Phase 3 Task 9 cycle 3b
/// sub-cycle 3 — <see langword="true"/> when the soft wrap fired at
/// a hyphenation candidate (a soft-hyphen U+00AD or a Liang-pattern
/// position under <see cref="Hyphens.Auto"/>). The painter (Phase 4)
/// uses this to decide whether to RENDER a visible hyphen glyph at
/// the end of the line per CSS Text L3 §6.1.1: "When a line is
/// broken at a hyphenation opportunity that does not include a
/// visible hyphen character, the UA must insert one." Always
/// <see langword="false"/> when <see cref="EndsWithMandatoryBreak"/>
/// is <see langword="true"/> (mandatory wins). Cycle 3b sub-cycle 3
/// only annotates the metadata; the actual visible-hyphen rendering
/// lands in Phase 4's display-list IR.</param>
internal readonly record struct LineFragment(
    ShapedRunSlice[] Slices,
    double TotalAdvance,
    bool EndsWithMandatoryBreak,
    bool EndsWithHyphenationBreak = false);
