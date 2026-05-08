// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;

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
///   <item><see cref="InlineSize"/> + <see cref="BlockSize"/> — the
///   margin-box extent along each axis. The painter uses these +
///   the box's <see cref="Box.Style"/> to derive padding/border/
///   content rectangles.</item>
/// </list>
///
/// <para><b>Cycle 1 limitations</b> (Phase 3 Task 7 cycle 1):</para>
/// <list type="bullet">
///   <item>The fragment is the MARGIN BOX. Padding + border + content
///   rectangles aren't separately encoded; Phase 4's painter computes
///   them by subtracting the corresponding style values.</item>
///   <item>No transformations applied (<c>transform</c> is Phase 3
///   Task 19+'s concern).</item>
///   <item>No clipping rectangle (<c>overflow: clip</c> /
///   <c>clip-path</c> deferred to a later task).</item>
/// </list>
/// </summary>
/// <param name="Box">The box this fragment represents. Reference
/// identity preserved so the painter can look up styles, content,
/// and pseudo-element classifications.</param>
/// <param name="InlineOffset">Inline-axis offset from the
/// fragmentainer's content-area origin (CSS px).</param>
/// <param name="BlockOffset">Block-axis offset from the
/// fragmentainer's content-area origin (CSS px).</param>
/// <param name="InlineSize">Margin-box extent along the inline
/// axis (CSS px).</param>
/// <param name="BlockSize">Margin-box extent along the block
/// axis (CSS px).</param>
internal readonly record struct BoxFragment(
    Box Box,
    double InlineOffset,
    double BlockOffset,
    double InlineSize,
    double BlockSize);
