// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// integration.</b> The new <see cref="InlineLines"/> field carries
/// the wrapped <see cref="LineFragment"/>[] for fragments emitted by
/// <c>BlockLayouter.LayoutInlineContent</c> (block containers whose
/// children are entirely inline-level). Null for pure-block
/// fragments — backwards compatible with cycles 1-2c that emitted
/// border-box-only rectangles. The painter (Phase 4) consumes the
/// line array to position shaped-glyph slices at successive baselines
/// inside the fragment's border box.</para>
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
///   <item>No clipping rectangle (<c>overflow: clip</c> /
///   <c>clip-path</c> deferred to a later task).</item>
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
/// <param name="InlineLines">Per Phase 3 Task 11 cycle 1 sub-cycle 1
/// — the wrapped line array for inline-only block fragments
/// produced by <c>BlockLayouter.LayoutInlineContent</c>. Each
/// <see cref="LineFragment"/> carries one wrapped line's
/// <c>ShapedRunSlice</c>s in document order; the painter (Phase 4)
/// positions these at successive baselines inside the fragment's
/// border box. <see langword="null"/> for pure-block fragments
/// (cycles 1-2c output, plus block containers whose children are
/// themselves block-level). Non-null + length 0 isn't a valid
/// state — empty inline content produces no fragment at all.</param>
internal readonly record struct BoxFragment(
    Box Box,
    double InlineOffset,
    double BlockOffset,
    double InlineSize,
    double BlockSize,
    LineFragment[]? InlineLines = null);
