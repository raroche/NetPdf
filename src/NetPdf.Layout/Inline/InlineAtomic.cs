// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Inline;

/// <summary>
/// Inline-atomic-boxes cycle (CSS Display 3 §2.4 "atomic inline-level box") — an opaque, indivisible
/// inline-level box that participates in line layout as a single unit, carrying its full box model so
/// the line reserves the right advance and the emitted fragment is a BORDER box. The first consumer is
/// an inline <c>&lt;img&gt;</c> (<see cref="BoxKind.InlineReplacedElement"/>).
///
/// <para><b>Box model (post-PR-#186 review P1).</b> The image's used CONTENT width/height (written into
/// the box's style slots by the pre-layout replaced-size pass) is wrapped by the box's padding, border,
/// and margin: <see cref="AdvancePx"/> is the MARGIN-box inline size (the line advance);
/// <see cref="BorderBoxWidthPx"/> / <see cref="BorderBoxHeightPx"/> are the emitted fragment's size (a
/// BORDER box — <c>ImagePainter</c> subtracts the image's own padding + border to recover the content
/// box); the leading <see cref="MarginInlineStartPx"/> offsets the border box within the advance, and
/// the block margins position it vertically (the margin-box bottom sits on the line's text baseline,
/// CSS 2.2 §10.8). For a plain inline image (no padding/border/margin) advance == border box == content,
/// so the geometry is byte-identical to the first cut.</para>
/// </summary>
/// <param name="Box">The atomic's source box (e.g. the inline <c>&lt;img&gt;</c>). The painter looks it
/// up in the image cache; the emitted fragment carries it so painting is geometry-driven.</param>
/// <param name="AdvancePx">The MARGIN-box inline size (CSS px) — the line advance the atomic reserves.</param>
/// <param name="BorderBoxWidthPx">The emitted fragment's inline size (the BORDER box).</param>
/// <param name="BorderBoxHeightPx">The emitted fragment's block size (the BORDER box). The line box
/// grows to fit the MARGIN box (this + the block margins).</param>
/// <param name="MarginInlineStartPx">The leading inline margin — offsets the border-box fragment within
/// the advance.</param>
/// <param name="MarginBlockStartPx">The top margin — part of the margin-box height the line accommodates.</param>
/// <param name="MarginBlockEndPx">The bottom margin — the margin-box bottom (border box + this) sits on
/// the text baseline.</param>
internal readonly record struct InlineAtomic(
    Box Box,
    double AdvancePx,
    double BorderBoxWidthPx,
    double BorderBoxHeightPx,
    double MarginInlineStartPx,
    double MarginBlockStartPx,
    double MarginBlockEndPx)
{
    /// <summary>The MARGIN-box block size (CSS px) — the atomic's contribution to the line-box height.</summary>
    public double MarginBoxHeightPx => MarginBlockStartPx + BorderBoxHeightPx + MarginBlockEndPx;
}
