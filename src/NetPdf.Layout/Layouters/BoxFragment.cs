// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Inline;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 7 — the layouter's per-box output: a
/// fully-positioned + fully-sized rectangle in the fragmentainer's
/// coordinate space (CSS px from the page content-area origin).
///
/// <para>Fragments are emitted into an
/// <see cref="IBlockFragmentSink"/> as the layouter walks the box
/// tree. Phase 4 (paint) consumes the emitted fragment list +
/// resolves it to PDF content streams.</para>
///
/// <para><b>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — inline content
/// integration.</b> The new <see cref="InlineLayout"/> field carries
/// the <see cref="InlineLayoutResult"/> for fragments emitted by
/// <c>BlockLayouter.LayoutInlineContent</c> (block containers whose
/// children are entirely inline-level). Null for pure-block
/// fragments — backwards compatible with cycles 1-2c that emitted
/// border-box-only rectangles. The painter (Phase 4) consumes the
/// bundle's <see cref="InlineLayoutResult.Lines"/> +
/// <see cref="InlineLayoutResult.ShapedRuns"/> +
/// <see cref="InlineLayoutResult.PreprocessedRuns"/> to position
/// shaped-glyph slices at successive baselines inside the fragment's
/// border box. Per sub-cycle 1 review Finding #1 — bundling the
/// glyph data with the lines (instead of returning bare
/// <c>LineFragment[]</c>) is mandatory because each line's slices
/// only carry indices into the <c>ShapedRuns</c> array; the painter
/// needs the array itself to resolve actual glyphs.</para>
///
/// <para><b>Per Phase 3 Task 7 cycle 1 PR #22 review fix #3 +
/// Copilot #2 — geometry IS the BORDER BOX</b> (not the margin
/// box). Cycle 1 originally documented "margin box" but the
/// inline-axis math used border-box subtraction (margin-left/right
/// stripped) while the block-axis math used margin-box addition
/// (margin-top/bottom included) — inconsistent. This revision
/// commits to the border box on BOTH axes:</para>
/// <list type="bullet">
///   <item><see cref="InlineSize"/> = <c>contentInlineSize -
///   marginInlineStart - marginInlineEnd</c> (border-box width).</item>
///   <item><see cref="BlockSize"/> = <c>borderBlockStart +
///   paddingBlockStart + contentBlock + paddingBlockEnd +
///   borderBlockEnd</c> (border-box height — does NOT include
///   margins).</item>
///   <item><see cref="InlineOffset"/> = position of the border-box
///   inline-start edge from the fragmentainer's content-area origin
///   (= <c>marginInlineStart</c> in cycle 1).</item>
///   <item><see cref="BlockOffset"/> = position of the border-box
///   block-start edge (= <c>fragmentainer.UsedBlockSize +
///   marginBlockStart</c> in cycle 1).</item>
/// </list>
///
/// <para>Pagination accounting (the layouter's
/// <c>fragmentainer.UsedBlockSize</c> advancement) uses the
/// MARGIN BOX block extent — but the fragment record stores the
/// BORDER BOX so the painter has the rectangle to draw without
/// re-deriving it. The painter reads the box's
/// <see cref="Box.Style"/> to derive padding + content rectangles
/// by subtraction.</para>
///
/// <para><b>Coordinate space.</b> Per CSS Logical Properties L1 +
/// the Phase 3 plan's block-axis convention:</para>
/// <list type="bullet">
///   <item><see cref="InlineOffset"/> — offset along the inline axis
///   from the fragmentainer's content-area origin. In
///   <c>horizontal-tb</c> writing mode this is X (left edge); in
///   <c>vertical-rl/lr</c> it's Y.</item>
///   <item><see cref="BlockOffset"/> — offset along the block
///   (fragmentation) axis. In <c>horizontal-tb</c> this is Y (top
///   edge); in <c>vertical-rl/lr</c> it's X.</item>
/// </list>
///
/// <para><b>Cycle 1 limitations</b> (Phase 3 Task 7 cycle 1):</para>
/// <list type="bullet">
///   <item>No transformations applied (<c>transform</c> is Phase 3
///   Task 19+'s concern).</item>
///   <item>BODY fragments carry no clipping rectangle (the body's
///   <c>overflow: clip</c>/<c>hidden</c> + <c>clip-path</c> stay
///   deferred); the opt-in <see cref="ClipRect"/> (margin-box
///   overflow clip-path cycle) is currently set only by the
///   page-margin-box painter.</item>
///   <item>Logical-axis property reads default to physical
///   <c>top</c>/<c>bottom</c>/<c>left</c>/<c>right</c> regardless of
///   <c>writing-mode</c> (PR #22 review fix #5 deferred to cycle 3).</item>
/// </list>
/// </summary>
/// <param name="Box">The box this fragment represents. Reference
/// identity preserved so the painter can look up styles, content,
/// and pseudo-element classifications.</param>
/// <param name="InlineOffset">Inline-axis offset of the BORDER-BOX
/// inline-start edge from the fragmentainer's content-area origin
/// (CSS px).</param>
/// <param name="BlockOffset">Block-axis offset of the BORDER-BOX
/// block-start edge from the fragmentainer's content-area origin
/// (CSS px).</param>
/// <param name="InlineSize">Border-box extent along the inline
/// axis (CSS px). Excludes margin.</param>
/// <param name="BlockSize">Border-box extent along the block
/// axis (CSS px). Excludes margin.</param>
/// <param name="InlineLayout">Per Phase 3 Task 11 cycle 1 sub-cycle 1
/// review Finding #1 — the inline pass result (lines + shaped runs +
/// preprocessed source runs) for inline-only block fragments produced
/// by <c>BlockLayouter.LayoutInlineContent</c>. The painter (Phase 4)
/// resolves each line's <see cref="ShapedRunSlice"/> via
/// <see cref="InlineLayoutResult.ShapedRuns"/>[<see cref="ShapedRunSlice.ShapedRunIndex"/>]
/// + walks the slice's glyph range; per-glyph styling is read from
/// the source <see cref="TextRun"/> at
/// <see cref="InlineLayoutResult.PreprocessedRuns"/>[<see cref="ItemizedRun.SourceTextRunIndex"/>].
/// <see langword="null"/> for pure-block fragments (cycles 1-2c
/// output, plus block containers whose children are themselves
/// block-level). Empty inline content produces no fragment at
/// all.</param>
/// <param name="LineAlignFactor">Per Phase 3 Task 21 (wrapped-line content-alignment) — an OPT-IN
/// per-line inline-alignment factor (0 = start/left, 0.5 = center, 1 = end/right). When non-zero the
/// painter shifts EACH painted line by <c>(InlineSize − lineAdvance) × LineAlignFactor</c> so a wrapped
/// multi-line run is aligned per line, not just block-left. DEFAULT 0 leaves every other fragment
/// (the body inline path, which doesn't yet apply <c>text-align</c>) byte-identical; only the page-margin
/// box sets it (to its resolved alignment). A single-line run's offset reduces to the block-level
/// alignment the caller would otherwise have pre-applied, so single-line output is unchanged.</param>
/// <param name="TextMetricsStyle">Per Phase 3 Task 23 (post-PR-#151 review P1) — an OPT-IN
/// <see cref="ComputedStyle"/> the painter uses for the TEXT line metrics (line-height / baseline /
/// line pitch) instead of <see cref="Box"/>'s style, while <see cref="Box"/>'s style still drives the
/// border/padding origin + decoration. <see langword="null"/> (the default) → the painter uses the box
/// style for both, so every other fragment is byte-identical. The page-margin box sets it to the CONTENT
/// style when a standalone <c>element(name)</c> renders the running element in its OWN font: the glyphs
/// are shaped at the element's font-size, so the line pitch + baseline must use that size too — otherwise
/// a 32px running header would paint 32px glyphs at the box's default 16px pitch and overlap.</param>
/// <param name="ClipRect">Per Phase 3 Task 21 (margin-box overflow clip-path cycle) — an OPT-IN
/// rectangle the painter clips this fragment's TEXT to (a PDF <c>q … re W n</c> / <c>Q</c> pair around
/// the fragment's glyph runs), in the same content-area-relative CSS px as
/// <see cref="InlineOffset"/>/<see cref="BlockOffset"/>. <see langword="null"/> (the default) paints
/// unclipped — every other fragment is byte-identical. The page-margin box sets it (to its padding box)
/// when its content overflows, so protruding glyphs clip at the box edge instead of spilling over the
/// page; an explicit <c>overflow: visible</c> on the box leaves it unset.</param>
/// <param name="PerLineBaselineTopPx">Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1) — when a
/// line carries a baseline-aligned inline-block, the line is sized by max-ascent / max-descent and this
/// gives the baseline's offset from the line top; the painter places that line's text on it (so text and
/// box share a baseline). A per-line <c>NaN</c> (and the null default) → the painter's real-metric
/// baseline, byte-identical.</param>
/// <param name="PerLineHeightsPx">Per-line PITCH (segment-pitch cycle) — line i advances by
/// its own height (a 32px h1 line over a 16px subtitle). Null = the uniform
/// <paramref name="TextMetricsStyle"/> pitch, byte-identical.</param>
/// <param name="PerLineAlignFactors">Per-line ALIGNMENT (segment-align cycle) — line i aligns by
/// its own factor. Null = the fragment-wide <paramref name="LineAlignFactor"/>.</param>
/// <param name="PerLineTopOffsetsPx">Per-line TOP GAPS (segment-margins cycle) — line i is pushed
/// down by its offset before placement (a leaf block's collapsed vertical margin; the band the
/// painter emits starts AFTER the gap, margins staying transparent). Null = no gaps.</param>
/// <param name="PerLineInsetLeftPx">Per-line LEFT INSETS (hpadding cycle) — line i's glyphs start
/// at its inset (a leaf block's own padding-left). Null = none.</param>
/// <param name="PerLineInsetRightPx">Per-line RIGHT INSETS (hpadding cycle) — together with the
/// left inset, shrinks line i's alignment extent. Null = none.</param>
/// <param name="SuppressBoxDecoration">Non-block-pagination arc — when true the painter skips this
/// fragment's BOX DECORATION (background, borders, outline) and paints only its text; set on the
/// inline-only-root content fragment a flex / grid item emits (box == the item) so the decoration the
/// item's GEOMETRY fragment already painted isn't painted twice. Default false = byte-identical.</param>
/// <param name="JustifyLines">text-align: justify cycle — when true the painter JUSTIFIES each line
/// except the last and any forced-break-terminated line (CSS Text 3 §7.3 inter-word distribution):
/// it splits the line's glyphs at word-separator spaces and spreads the free space across the gaps.
/// Default false leaves every non-justified fragment byte-identical.</param>
internal readonly record struct BoxFragment(
    Box Box,
    double InlineOffset,
    double BlockOffset,
    double InlineSize,
    double BlockSize,
    InlineLayoutResult? InlineLayout = null,
    double LineAlignFactor = 0.0,
    ComputedStyle? TextMetricsStyle = null,
    FragmentClipRect? ClipRect = null,
    // Per-line PITCH (segment-pitch cycle): line i advances by PerLineHeightsPx[i] instead of the
    // uniform TextMetricsStyle pitch — a 32px h1 line over a 16px subtitle each gets its own
    // height. Null (the default) = the uniform pitch, byte-identical.
    System.Collections.Generic.IReadOnlyList<double>? PerLineHeightsPx = null,
    // Inline-block last-line-baseline cycle (CSS 2.2 §10.8.1): line i's text baseline is at
    // PerLineBaselineTopPx[i] from the line top (when a baseline-aligned inline-block forced the
    // max-ascent line-box on it). A per-line NaN — and the null default — keeps the painter's
    // real-metric centred baseline, byte-identical.
    System.Collections.Generic.IReadOnlyList<double>? PerLineBaselineTopPx = null,
    // Per-line ALIGNMENT (segment-align cycle): line i aligns by PerLineAlignFactors[i] instead of
    // the fragment-wide LineAlignFactor. Null = the uniform factor, byte-identical.
    System.Collections.Generic.IReadOnlyList<double>? PerLineAlignFactors = null,
    // Per-line TOP GAPS (segment-margins cycle): line i is pushed down by PerLineTopOffsetsPx[i]
    // BEFORE it is placed (a leaf block's collapsed vertical margin). Null = no gaps.
    System.Collections.Generic.IReadOnlyList<double>? PerLineTopOffsetsPx = null,
    // Per-line HORIZONTAL INSETS (hpadding cycle): line i's glyphs start at PerLineInsetLeftPx[i]
    // and its alignment extent shrinks by both insets (a leaf block's own horizontal padding).
    // Null = no insets.
    System.Collections.Generic.IReadOnlyList<double>? PerLineInsetLeftPx = null,
    System.Collections.Generic.IReadOnlyList<double>? PerLineInsetRightPx = null,
    // Non-block-pagination arc (flex item / grid cell CONTENT layout): when true the painter
    // skips this fragment's BOX DECORATION (background-color / -image, borders, outline) and
    // paints only its <see cref="InlineLayout"/> text. Set on the inline-only-root content
    // fragment a flex / grid item emits (box == the item) — the item's flex / grid GEOMETRY
    // fragment already paints the decoration, so without this the background / border would
    // paint TWICE (benign for an opaque background, visibly doubled for a translucent one).
    // DEFAULT false leaves every other fragment byte-identical.
    bool SuppressBoxDecoration = false,
    // text-align: justify cycle — when true the painter JUSTIFIES each line (except the last + any
    // line ending at a forced break): it splits the line's glyphs at word-separator spaces and
    // distributes the free space (InlineSize − line advance) across the inter-word gaps. The
    // per-line LineAlignFactor still applies to the non-justified last line (justify → factor 0 =
    // start). DEFAULT false leaves every non-justified fragment byte-identical.
    bool JustifyLines = false);

/// <summary>An axis-aligned fragment clip rectangle (content-area-relative CSS px, y-down — the
/// <see cref="BoxFragment.InlineOffset"/>/<see cref="BoxFragment.BlockOffset"/> space). See
/// <see cref="BoxFragment.ClipRect"/>.</summary>
internal readonly record struct FragmentClipRect(double LeftPx, double TopPx, double WidthPx, double HeightPx);
