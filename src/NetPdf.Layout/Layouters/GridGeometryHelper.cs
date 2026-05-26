// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Per Phase 3 Task 17 cycle 1 — shared content-box geometry
/// computation for grid containers. Mirrors
/// <see cref="FlexGeometryHelper"/>'s structure exactly (= one method
/// returning a four-field record struct); the math is identical (the
/// content box is the border-box minus the wrapper's own borders +
/// paddings). The dispatcher calls this BEFORE
/// <c>GridLayouter.ConfigureEmission</c> so the layouter receives the
/// content-area geometry the items lay out within.
///
/// <para><b>Why a standalone helper, not extended into the dispatcher.</b>
/// The geometry computation runs BEFORE the dispatch (the caller may
/// need it for pre-dispatch decisions in later cycles — e.g., the
/// cycle-5 paginatable-grid pre-grow clamp will need geometry to
/// decide if the container fits the page-remaining-block). Mirrors
/// <see cref="FlexGeometryHelper"/>'s pattern at the same site in
/// BlockLayouter; the cycle-4d (PR-#82 review #2) extraction made
/// FlexGeometryHelper its own type for the same reason.</para>
///
/// <para><b>Difference from FlexGeometryHelper.</b> None for cycle 1.
/// Cycle 1's grid geometry derivation is exactly the same as flex's
/// (= wrapper's border-box minus wrapper's own chrome). Later cycles
/// (5+ for multi-page grids) may diverge if grid needs
/// fragmentainer-remaining derivation similar to multicol's
/// auto-height branch; for now the two helpers have identical
/// internals, kept separate so future divergence doesn't have to
/// reverse a shared abstraction.</para></summary>
internal static class GridGeometryHelper
{
    /// <summary>Compute the grid container's content-box geometry
    /// from the wrapper's border-box geometry. See
    /// <see cref="FlexGeometryHelper.ComputeContentGeometry"/> for the
    /// arithmetic (= 4 borders + 4 paddings read from the wrapper's
    /// computed style; size dimensions floored at 1.0 to defend
    /// against <c>GridLayouter.ConfigureEmission</c>'s positive-size
    /// validation when chrome exceeds border-box).</summary>
    /// <param name="gridBox">The grid container box. Its computed
    /// style provides the border + padding reads on all 4 sides.</param>
    /// <param name="borderBoxInlineSize">Wrapper's resolved inline extent.</param>
    /// <param name="borderBoxBlockSize">Wrapper's resolved block extent.</param>
    /// <param name="borderBoxInlineOffset">Wrapper's BoxFragment inline offset.</param>
    /// <param name="borderBoxBlockOffset">Wrapper's BoxFragment block offset.</param>
    /// <returns>The content-box geometry that
    /// <c>GridLayouter.ConfigureEmission</c> expects.</returns>
    public static GridContentGeometry ComputeContentGeometry(
        Box gridBox,
        double borderBoxInlineSize,
        double borderBoxBlockSize,
        double borderBoxInlineOffset,
        double borderBoxBlockOffset)
    {
        var borderInlineStart =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var paddingInlineStart =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var borderInlineEnd =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingInlineEnd =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
        var borderBlockStart =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var paddingBlockStart =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var borderBlockEnd =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var paddingBlockEnd =
            gridBox.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);

        var contentInlineSize = System.Math.Max(1.0,
            borderBoxInlineSize
            - borderInlineStart - paddingInlineStart
            - borderInlineEnd - paddingInlineEnd);
        var contentBlockSize = System.Math.Max(1.0,
            borderBoxBlockSize
            - borderBlockStart - paddingBlockStart
            - borderBlockEnd - paddingBlockEnd);
        var contentInlineOffset =
            borderBoxInlineOffset + borderInlineStart + paddingInlineStart;
        var contentBlockOffset =
            borderBoxBlockOffset + borderBlockStart + paddingBlockStart;

        return new GridContentGeometry(
            ContentInlineSize: contentInlineSize,
            ContentBlockSize: contentBlockSize,
            ContentInlineOffset: contentInlineOffset,
            ContentBlockOffset: contentBlockOffset);
    }
}

/// <summary>Per Phase 3 Task 17 cycle 1 — content-box geometry of a
/// grid container, computed from the wrapper's border-box by
/// <see cref="GridGeometryHelper.ComputeContentGeometry"/>. The
/// four-tuple shape matches the parameters
/// <c>GridLayouter.ConfigureEmission</c> expects.</summary>
internal readonly record struct GridContentGeometry(
    double ContentInlineSize,
    double ContentBlockSize,
    double ContentInlineOffset,
    double ContentBlockOffset);
