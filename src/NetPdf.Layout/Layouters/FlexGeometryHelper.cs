// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;

namespace NetPdf.Layout.Layouters;

/// <summary>Per Phase 3 Task 16 cycle 4d (PR-#82 review #2 follow-on)
/// — shared content-box geometry computation for flex containers.
///
/// <para><b>Pre-cycle-4d duplication.</b> The three BlockLayouter
/// dispatch sites that route into <see cref="FlexLayouter"/> via
/// <c>DispatchFlexInner</c> each duplicated ~10 lines of border /
/// padding reads + content-box derivation:</para>
/// <list type="bullet">
///   <item><b>Outer dispatch</b> (line ~2429) — direct flex child of
///   the BlockLayouter's root.</item>
///   <item><b>Recursive dispatch</b> (line ~3460) — nested flex
///   container reached via <c>EmitBlockSubtreeRecursive</c>.</item>
///   <item><b>Forced-overflow re-route</b> (line ~1991, added in
///   cycle 4b post-PR-#83 review P1 #2) — flex container that hit
///   the forced-overflow path because it was ineligible for the
///   paginatable-flex clamp.</item>
/// </list>
/// <para>The geometry math is identical across all three sites
/// (the wrapper's border-box minus the wrapper's own borders +
/// paddings gives the content area for FlexLayouter); the
/// duplication was a P2 review finding on PR-#82. Cycle 4d
/// consolidates the math here.</para>
///
/// <para><b>Why a standalone helper, not extended
/// <c>DispatchFlexInner</c>.</b> The geometry computation runs
/// BEFORE the dispatch (the caller needs the content-box geometry
/// for the dispatch arguments AND for any pre-dispatch decisions
/// like the cycle 4b paginatable-flex clamp). Mirrors
/// <c>MulticolGeometryHelper</c>'s pattern at the same site in
/// BlockLayouter.</para>
///
/// <para><b>Difference from <c>MulticolGeometryHelper</c>.</b>
/// Multicol has an auto-height path that derives the per-column
/// block-size from the fragmentainer-remaining space (= "fill
/// available column space" semantics per CSS Multi-column L1
/// §3.5). Flex's content-block-size is always derived from the
/// wrapper's border-box (which may have been clamped by the
/// cycle 4b paginatable-flex pre-grow clamp at the caller site);
/// no fragmentainer-remaining derivation here. Simpler than
/// multicol's branched shape.</para></summary>
internal static class FlexGeometryHelper
{
    /// <summary>Compute the flex container's content-box geometry
    /// from the wrapper's border-box geometry.
    ///
    /// <para><b>Math:</b>
    /// <list type="bullet">
    ///   <item><c>contentInlineSize = max(1,
    ///   borderBoxInlineSize - borderInlineStart - paddingInlineStart -
    ///   borderInlineEnd - paddingInlineEnd)</c></item>
    ///   <item><c>contentBlockSize = max(1,
    ///   borderBoxBlockSize - borderBlockStart - paddingBlockStart -
    ///   borderBlockEnd - paddingBlockEnd)</c></item>
    ///   <item><c>contentInlineOffset = borderBoxInlineOffset +
    ///   borderInlineStart + paddingInlineStart</c></item>
    ///   <item><c>contentBlockOffset = borderBoxBlockOffset +
    ///   borderBlockStart + paddingBlockStart</c></item>
    /// </list>
    /// The 1-px floor on the size dimensions defends against
    /// FlexLayouter's <c>ConfigureEmission</c> argument validation
    /// (which throws on non-positive content sizes) when a flex
    /// container's chrome (= borders + padding) exceeds its
    /// declared border-box size; the result is a degenerate 1×1
    /// content area but no exception.</para></summary>
    /// <param name="flexBox">The flex container box. Its computed
    /// style provides the border + padding reads on all 4 sides.</param>
    /// <param name="borderBoxInlineSize">The wrapper's resolved
    /// inline extent (= what the caller computed for the wrapper's
    /// BoxFragment emission). Pre-cycle-4b this was the natural
    /// content extent; post-cycle-4b the paginatable-flex clamp may
    /// have shrunk it to the page-remaining-block.</param>
    /// <param name="borderBoxBlockSize">The wrapper's resolved
    /// block extent (subject to the same cycle-4b clamp).</param>
    /// <param name="borderBoxInlineOffset">Wrapper's BoxFragment
    /// inline offset (= where the painter puts the wrapper).</param>
    /// <param name="borderBoxBlockOffset">Wrapper's BoxFragment
    /// block offset.</param>
    /// <returns>The content-box geometry that
    /// <c>FlexLayouter.ConfigureEmission</c> expects.</returns>
    public static FlexContentGeometry ComputeContentGeometry(
        Box flexBox,
        double borderBoxInlineSize,
        double borderBoxBlockSize,
        double borderBoxInlineOffset,
        double borderBoxBlockOffset)
    {
        var borderInlineStart =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth);
        var paddingInlineStart =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft);
        var borderInlineEnd =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.BorderRightWidth);
        var paddingInlineEnd =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.PaddingRight);
        var borderBlockStart =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.BorderTopWidth);
        var paddingBlockStart =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.PaddingTop);
        var borderBlockEnd =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);
        var paddingBlockEnd =
            flexBox.Style.ReadLengthPxOrZero(PropertyId.PaddingBottom);

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

        return new FlexContentGeometry(
            ContentInlineSize: contentInlineSize,
            ContentBlockSize: contentBlockSize,
            ContentInlineOffset: contentInlineOffset,
            ContentBlockOffset: contentBlockOffset);
    }
}

/// <summary>Per Phase 3 Task 16 cycle 4d — content-box geometry of
/// a flex container, computed from the wrapper's border-box by
/// <see cref="FlexGeometryHelper.ComputeContentGeometry"/>. The
/// four-tuple shape matches the parameters
/// <c>FlexLayouter.ConfigureEmission</c> expects (= one allocation
/// per dispatch instead of a four-tuple positional return).</summary>
internal readonly record struct FlexContentGeometry(
    double ContentInlineSize,
    double ContentBlockSize,
    double ContentInlineOffset,
    double ContentBlockOffset);
