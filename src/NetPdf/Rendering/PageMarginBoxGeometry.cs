// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Rendering;

/// <summary>
/// Computes the page-box rectangle + text alignment for each CSS Paged Media L3 §6.4 margin box,
/// given the resolved page size + margins (CSS px, page-top-left origin, y-down). Phase 3 Task 21
/// cycle 3.
/// </summary>
/// <remarks>
/// <para>
/// The 16 boxes tile the page's margin area: 4 corner boxes (each the corresponding
/// margin-width × margin-height rectangle) and, per edge, 3 boxes that share the edge band.
/// </para>
/// <para>
/// <b>Placement (§5.3).</b> This type returns each box's full edge BAND + its name-derived
/// alignment + the §5.3 <see cref="MarginBoxAxis"/> (the dimension that varies), and resolves sibling
/// OVERLAP on an edge via <see cref="ResolveEdgeOverlap"/>. The painter sizes a content-bearing box to
/// its content (shrink-to-fit) or to an explicit <c>width</c>/<c>height</c> along that axis (top/bottom
/// edges → width; left/right → height) so its background/border cover the box, positions it in the band
/// by its name-derived role (start / center / end), and — when siblings would overlap —
/// <see cref="ResolveEdgeOverlap"/> resolves them per §5.3.2: the center box B is kept CENTERED (flexed
/// against an imaginary 2x-max(A, C) box) with A/C sized into the side gaps; with no center box the two
/// sides flex (interpolate min-to-max, or proportional to min-content when the mins don't fit). The painter
/// then RE-WRAPS a flexed/shrunk box's content to its assigned width (multi-line, aligned per line) so it
/// fits. Corner boxes + empty boxes keep the full band. STILL DEFERRED: vertical-edge
/// (height) overflow, and `box-sizing` — content narrower than its longest unbreakable word still
/// overflows that word (deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class PageMarginBoxGeometry
{
    /// <summary>Which dimension of an edge box is the §5.3 VARIABLE one (shrink-to-fit to content),
    /// the perpendicular one being fixed to the margin: <see cref="Horizontal"/> for the top/bottom
    /// edges (width varies, distributed among the 3 boxes along the edge), <see cref="Vertical"/> for
    /// the left/right edges (height varies), <see cref="None"/> for the 4 corner boxes (both fixed to
    /// the margin × margin rect).</summary>
    internal enum MarginBoxAxis { None, Horizontal, Vertical }

    /// <summary>A margin box's page-px BAND rectangle (the full edge extent) plus the fraction of
    /// leftover space placed before the content on each axis (0 = start, 0.5 = center, 1 = end) and
    /// its <see cref="MarginBoxAxis"/> (the dimension that shrinks to content). The painter sizes the
    /// box to its content along <see cref="VariableAxis"/>, positions it in the band by
    /// <see cref="HAlign"/> / <see cref="VAlign"/>, and aligns the line within it on the fixed axis.</summary>
    internal readonly record struct MarginBoxRegion(
        double X, double Y, double Width, double Height, double HAlign, double VAlign, MarginBoxAxis VariableAxis);

    private const double Start = 0.0;
    private const double Center = 0.5;
    private const double End = 1.0;

    /// <summary>Resolve the region for <paramref name="name"/> (a canonical lowercased margin-box
    /// name) against the page geometry. Returns <see langword="false"/> for an unknown name.</summary>
    public static bool TryGetRegion(
        string name, double pageWidthPx, double pageHeightPx,
        double marginTopPx, double marginRightPx, double marginBottomPx, double marginLeftPx,
        out MarginBoxRegion region)
    {
        // Edge bands span the content extent between the perpendicular margins. Clamp to >= 0 so
        // margins exceeding the page size can't produce negative band widths/heights → negative
        // region sizes + non-finite PDF coords downstream (post-PR-#132 review; mirrors the
        // pipeline's body content-box clamp).
        var contentWidth = Math.Max(0, pageWidthPx - marginLeftPx - marginRightPx);
        var contentHeight = Math.Max(0, pageHeightPx - marginTopPx - marginBottomPx);
        var rightX = pageWidthPx - marginRightPx;
        var bottomY = pageHeightPx - marginBottomPx;

        MarginBoxRegion? r = name switch
        {
            // Corners — fixed both dimensions (the margin × margin rectangle); center the line.
            "top-left-corner" => new(0, 0, marginLeftPx, marginTopPx, Center, Center, MarginBoxAxis.None),
            "top-right-corner" => new(rightX, 0, marginRightPx, marginTopPx, Center, Center, MarginBoxAxis.None),
            "bottom-left-corner" => new(0, bottomY, marginLeftPx, marginBottomPx, Center, Center, MarginBoxAxis.None),
            "bottom-right-corner" => new(rightX, bottomY, marginRightPx, marginBottomPx, Center, Center, MarginBoxAxis.None),

            // Top edge band — width varies (shrink-to-fit), positioned by name.
            "top-left" => new(marginLeftPx, 0, contentWidth, marginTopPx, Start, Center, MarginBoxAxis.Horizontal),
            "top-center" => new(marginLeftPx, 0, contentWidth, marginTopPx, Center, Center, MarginBoxAxis.Horizontal),
            "top-right" => new(marginLeftPx, 0, contentWidth, marginTopPx, End, Center, MarginBoxAxis.Horizontal),

            // Bottom edge band.
            "bottom-left" => new(marginLeftPx, bottomY, contentWidth, marginBottomPx, Start, Center, MarginBoxAxis.Horizontal),
            "bottom-center" => new(marginLeftPx, bottomY, contentWidth, marginBottomPx, Center, Center, MarginBoxAxis.Horizontal),
            "bottom-right" => new(marginLeftPx, bottomY, contentWidth, marginBottomPx, End, Center, MarginBoxAxis.Horizontal),

            // Left edge column — height varies (shrink-to-fit), positioned by name; center horizontally.
            "left-top" => new(0, marginTopPx, marginLeftPx, contentHeight, Center, Start, MarginBoxAxis.Vertical),
            "left-middle" => new(0, marginTopPx, marginLeftPx, contentHeight, Center, Center, MarginBoxAxis.Vertical),
            "left-bottom" => new(0, marginTopPx, marginLeftPx, contentHeight, Center, End, MarginBoxAxis.Vertical),

            // Right edge column.
            "right-top" => new(rightX, marginTopPx, marginRightPx, contentHeight, Center, Start, MarginBoxAxis.Vertical),
            "right-middle" => new(rightX, marginTopPx, marginRightPx, contentHeight, Center, Center, MarginBoxAxis.Vertical),
            "right-bottom" => new(rightX, marginTopPx, marginRightPx, contentHeight, Center, End, MarginBoxAxis.Vertical),

            _ => null,
        };
        region = r ?? default;
        return r is not null;
    }

    /// <summary>The MAX-content (or explicit) + MIN-content outer sizes (along the §5.3 VARIABLE axis) of
    /// the up-to-three boxes sharing one edge band, by name-derived role: A = start, B = center, C = end.
    /// <c>Desired…</c> is the max-content / explicit size, <c>Min…</c> the min-content (longest
    /// unbreakable run; <c>Min ≤ Desired</c>; <c>Min == Desired</c> for rigid / unbreakable / explicit
    /// content). An absent box (<c>Has… = false</c>) doesn't participate.</summary>
    internal readonly record struct EdgeTriple(
        double DesiredA, bool HasA, double DesiredB, bool HasB, double DesiredC, bool HasC,
        double MinA, double MinB, double MinC);

    /// <summary>Each resolved box's outer size + start offset along the variable axis (0 = the band's
    /// start). Absent boxes are (0, 0).</summary>
    internal readonly record struct ResolvedTriple(
        double SizeA, double StartA, double SizeB, double StartB, double SizeC, double StartC);

    /// <summary>
    /// CSS Paged Media L3 §5.3.2 — resolve the up-to-three page-margin boxes sharing one edge band so they
    /// don't OVERLAP. Each box is positioned by its name-derived role: A flush start, B centered, C flush
    /// end. When the boxes' desired sizes at those role positions would overlap:
    /// <list type="bullet">
    ///   <item><b>Center box present.</b> B is kept CENTERED (its center stays at the band's center). Its
    ///     used size is flexed against an imaginary "AC" box whose extent is <c>2 × max(A, C)</c> (the
    ///     doubling reserves symmetric side space, so the centre holds — §5.3.2's generated-B rule); A and
    ///     C are then sized within the equal side gaps left on each side of B. RIGID / over-constrained
    ///     content (no box can shrink, or even the min-contents don't fit) keeps B at its desired size and
    ///     clamps the sides into the gaps (a content box then overflows).</item>
    ///   <item><b>No center box.</b> The two side boxes share the band by the §5.3.2 flex: interpolate
    ///     each between min- and max-content when the mins fit, else distribute the band PROPORTIONALLY to
    ///     each box's min-content width (so long unbreakable content shrinks fairly, not to its max).</item>
    /// </list>
    /// When they DON'T overlap, the desired sizes + role positions are returned unchanged — so the common
    /// short-header/footer case stays byte-identical to the per-box (cycle 14/15) model. The shrunk widths
    /// drive the painter's content re-wrap so wrappable content fits; unbreakable content overflows.
    /// </summary>
    internal static ResolvedTriple ResolveEdgeOverlap(EdgeTriple boxes, double available)
    {
        var l = Math.Max(0, available);
        var dA = boxes.HasA ? Math.Clamp(boxes.DesiredA, 0, l) : 0;
        var dB = boxes.HasB ? Math.Clamp(boxes.DesiredB, 0, l) : 0;
        var dC = boxes.HasC ? Math.Clamp(boxes.DesiredC, 0, l) : 0;
        // Min-content per box, clamped to its (clamped) max so Min ≤ Desired always holds.
        var mA = boxes.HasA ? Math.Clamp(boxes.MinA, 0, dA) : 0;
        var mB = boxes.HasB ? Math.Clamp(boxes.MinB, 0, dB) : 0;
        var mC = boxes.HasC ? Math.Clamp(boxes.MinC, 0, dC) : 0;

        // Role positions (start offsets) of the DESIRED boxes: A flush start, B centered, C flush end.
        var startA = 0.0;
        var startB = (l - dB) / 2.0;
        var startC = l - dC;

        // Overlap when a box's extent crosses into the next box's start.
        var overlapAB = boxes.HasA && boxes.HasB && startA + dA > startB;
        var overlapBC = boxes.HasB && boxes.HasC && startB + dB > startC;
        var overlapAC = boxes.HasA && boxes.HasC && !boxes.HasB && dA + dC > l;
        if (!(overlapAB || overlapBC || overlapAC))
            // No overlap → unchanged. An ABSENT box reports (0, 0) per the contract (the caller ignores
            // absent boxes, but a clean contract keeps the unit tests unambiguous).
            return new ResolvedTriple(
                dA, startA, dB, boxes.HasB ? startB : 0.0, dC, boxes.HasC ? startC : 0.0);

        if (boxes.HasB)
        {
            // §5.3.2 — keep B CENTERED. Flex it against the imaginary AC box (2 × the larger side) so the
            // centre stays put; rigid / over-constrained content keeps B at its desired size and just
            // clamps the sides into the gaps. Either way A and C fill the equal gaps on each side of B.
            var canFlex = (mA < dA || mB < dB || mC < dC) && mA + mB + mC <= l;
            double sizeB;
            if (canFlex)
            {
                var acDesired = 2.0 * Math.Max(dA, dC);
                var acMin = 2.0 * Math.Max(mA, mC);
                (sizeB, _) = FlexPair(mB, dB, acMin, acDesired, l);
            }
            else
            {
                sizeB = dB; // rigid / mins-don't-fit: B keeps its (band-clamped) desired size, centered.
            }
            var centeredStartB = (l - sizeB) / 2.0;
            var sideGap = Math.Max(0, centeredStartB);          // equal gap on each side of the centered B
            var sizeA = boxes.HasA ? Math.Min(dA, sideGap) : 0.0;
            var sizeC = boxes.HasC ? Math.Min(dC, sideGap) : 0.0;
            return new ResolvedTriple(
                sizeA, 0.0, sizeB, centeredStartB, sizeC, boxes.HasC ? l - sizeC : 0.0);
        }

        // No center box — distribute the band between the two side boxes (A flush start, C flush end) by
        // the §5.3.2 flex: interpolate min→max when the mins fit, else proportional to min-content.
        var (resolvedA, resolvedC) = FlexPair(mA, dA, mC, dC, l);
        return new ResolvedTriple(
            resolvedA, 0.0, 0.0, 0.0, resolvedC, boxes.HasC ? l - resolvedC : 0.0);
    }

    /// <summary>
    /// CSS Page §5.3.2 flex of two boxes (each <c>min</c>..<c>max</c> outer size) into <paramref name="available"/>:
    /// <list type="bullet">
    ///   <item>min-contents don't fit (<c>Σmin ≥ available</c>) → distribute available PROPORTIONALLY to
    ///     each box's min-content (both then overflow, fairly);</item>
    ///   <item>otherwise (the callers only flex on overlap, so <c>available ≤ Σmax</c>) → give each box its
    ///     min plus a share of the slack proportional to <c>(max − min)</c>. The factor is capped at 1 so a
    ///     box never exceeds its max-content (the defensive grow case, unreached from the overlap paths).</item>
    /// </list>
    /// </summary>
    private static (double First, double Second) FlexPair(
        double min1, double max1, double min2, double max2, double available)
    {
        var l = Math.Max(0, available);
        var sumMin = min1 + min2;
        if (sumMin >= l)
            return sumMin > 0 ? (l * min1 / sumMin, l * min2 / sumMin) : (l / 2.0, l / 2.0);
        var span = (max1 + max2) - sumMin;
        if (span <= 0) return (min1, min2); // both rigid (degenerate; ∝min already covers Σmin ≥ l).
        var factor = Math.Min(1.0, (l - sumMin) / span);
        return (min1 + (max1 - min1) * factor, min2 + (max2 - min2) * factor);
    }
}
