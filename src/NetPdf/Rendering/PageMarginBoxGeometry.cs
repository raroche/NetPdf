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
/// <b>Placement (§5.3 shrink-to-fit, first cut).</b> This type returns each box's full edge BAND +
/// its name-derived alignment + the §5.3 <see cref="MarginBoxAxis"/> (the dimension that varies). The
/// painter then SHRINKS a content-bearing box to its content size along that axis (top/bottom edges →
/// width; left/right → height) so its background/border cover the box, and positions it in the band by
/// the alignment (start / center / end by name). Corner boxes + empty boxes keep the full band. The
/// FULL CSS §5.3 min/max-content DISTRIBUTION (resolving widths when sibling boxes would overlap),
/// explicit <c>width</c>/<c>height</c>, and overflow clipping stay deferred — so two long boxes on one
/// edge can still overlap and content overflowing a band isn't clipped (deferrals.md#layout-to-pdf-pipeline).
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
}
