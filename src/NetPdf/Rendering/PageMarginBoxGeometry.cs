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
/// <b>Placement (§5.3, first cut).</b> This type returns each box's full edge BAND + its name-derived
/// alignment + the §5.3 <see cref="MarginBoxAxis"/> (the dimension that varies), and resolves sibling
/// OVERLAP on an edge via <see cref="ResolveEdgeOverlap"/>. The painter sizes a content-bearing box to
/// its content (shrink-to-fit) or to an explicit <c>width</c>/<c>height</c> along that axis (top/bottom
/// edges → width; left/right → height) so its background/border cover the box, positions it in the band
/// by its name-derived role (start / center / end), and — when siblings would overlap —
/// <see cref="ResolveEdgeOverlap"/> clamps them apart (center-priority). Corner boxes + empty boxes keep
/// the full band. STILL DEFERRED: the spec-strict §5.3.2 min/max-content FLEX (this first cut uses each
/// box's max-content / explicit size + clamps rather than re-wrapping) and overflow clipping/wrapping —
/// so content wider than its clamped box still overflows (deferrals.md#layout-to-pdf-pipeline).
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

    /// <summary>The desired outer sizes (along the §5.3 VARIABLE axis) of the up-to-three boxes sharing
    /// one edge band, by name-derived role: A = start, B = center, C = end. An absent box
    /// (<c>Has… = false</c>) doesn't participate.</summary>
    internal readonly record struct EdgeTriple(
        double DesiredA, bool HasA, double DesiredB, bool HasB, double DesiredC, bool HasC);

    /// <summary>Each resolved box's outer size + start offset along the variable axis (0 = the band's
    /// start). Absent boxes are (0, 0).</summary>
    internal readonly record struct ResolvedTriple(
        double SizeA, double StartA, double SizeB, double StartB, double SizeC, double StartC);

    /// <summary>
    /// CSS Paged Media L3 §5.3 — resolve the up-to-three page-margin boxes sharing one edge band so they
    /// don't OVERLAP (first cut of the §5.3.2 distribution). Each box is positioned by its name-derived
    /// role: A flush start, B centered, C flush end. When the boxes' desired sizes at those positions
    /// would overlap, the CENTER box keeps its (band-clamped) size centered and the side boxes are
    /// CLAMPED to the gap on each side (center-priority); with NO center box, two overlapping side boxes
    /// shrink PROPORTIONALLY to share the band. When they DON'T overlap, the desired sizes + role
    /// positions are returned unchanged — so the common short-header/footer case stays byte-identical to
    /// the per-box (cycle 14/15) model.
    /// </summary>
    /// <remarks>
    /// Deterministic first cut: it uses each box's max-content / explicit outer size (no min-content
    /// measurement) and CLAMPS the box rather than re-wrapping its content, so content wider than its
    /// clamped box overflows — overflow clipping is a separate deferral (deferrals.md). The spec-strict
    /// §5.3.2 min/max-content flex (letting the center box shrink toward its min-content to give the
    /// sides more room) is a follow-up.
    /// </remarks>
    internal static ResolvedTriple ResolveEdgeOverlap(EdgeTriple boxes, double available)
    {
        var l = Math.Max(0, available);
        var dA = boxes.HasA ? Math.Clamp(boxes.DesiredA, 0, l) : 0;
        var dB = boxes.HasB ? Math.Clamp(boxes.DesiredB, 0, l) : 0;
        var dC = boxes.HasC ? Math.Clamp(boxes.DesiredC, 0, l) : 0;

        // Role positions (start offsets) of the DESIRED boxes: A flush start, B centered, C flush end.
        var startA = 0.0;
        var startB = (l - dB) / 2.0;
        var startC = l - dC;

        // Overlap when a box's extent crosses into the next box's start.
        var overlapAB = boxes.HasA && boxes.HasB && startA + dA > startB;
        var overlapBC = boxes.HasB && boxes.HasC && startB + dB > startC;
        var overlapAC = boxes.HasA && boxes.HasC && !boxes.HasB && dA + dC > l;
        if (!(overlapAB || overlapBC || overlapAC))
            return new ResolvedTriple(dA, startA, dB, startB, dC, startC); // no overlap → unchanged

        if (boxes.HasB)
        {
            // Center-priority: B keeps its (band-clamped) size, centered; A/C clamp to the side gaps.
            startB = (l - dB) / 2.0;
            dA = Math.Min(dA, Math.Max(0, startB));               // left gap  = [0, startB)
            dC = Math.Min(dC, Math.Max(0, l - (startB + dB)));    // right gap = (startB+dB, l]
        }
        else
        {
            // No center box: A (start) + C (end) overlap → shrink proportionally to share the band.
            var total = dA + dC;
            if (total > l && total > 0)
            {
                var scale = l / total;
                dA *= scale;
                dC *= scale;
            }
        }

        return new ResolvedTriple(dA, 0.0, dB, startB, dC, l - dC);
    }
}
