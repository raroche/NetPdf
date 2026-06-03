// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
/// <b>Cycle 3 placement.</b> The full CSS Page 3 §5.3 three-box-per-edge sizing (distributing the
/// edge among left/center/right by their auto/content/fixed widths) is deferred. Instead each
/// edge box is given the FULL edge band as its rectangle and the single content line is aligned
/// within it by the box's name: the <c>-left</c>/<c>-top</c> boxes align to the start, <c>-center</c>/
/// <c>-middle</c> to the center, <c>-right</c>/<c>-bottom</c> to the end; corner boxes center both
/// axes. So <c>top-left</c> / <c>top-center</c> / <c>top-right</c> don't collide unless their text
/// is wide enough to overlap (the §5.3 sizing that would prevent that is the deferred follow-up;
/// no clipping is applied — deferrals.md#layout-to-pdf-pipeline).
/// </para>
/// </remarks>
internal static class PageMarginBoxGeometry
{
    /// <summary>A margin box's page-px rectangle plus the fraction of leftover space placed before
    /// the content line on each axis (0 = start, 0.5 = center, 1 = end). The painter positions a
    /// line of width <c>w</c> at <c>X + (Width - w) * HAlign</c> and a line box of height <c>h</c>
    /// at <c>Y + (Height - h) * VAlign</c>.</summary>
    internal readonly record struct MarginBoxRegion(
        double X, double Y, double Width, double Height, double HAlign, double VAlign);

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
        // Edge bands span the content extent between the perpendicular margins.
        var contentWidth = pageWidthPx - marginLeftPx - marginRightPx;
        var contentHeight = pageHeightPx - marginTopPx - marginBottomPx;
        var rightX = pageWidthPx - marginRightPx;
        var bottomY = pageHeightPx - marginBottomPx;

        MarginBoxRegion? r = name switch
        {
            // Corners — center the line in the margin × margin rectangle.
            "top-left-corner" => new(0, 0, marginLeftPx, marginTopPx, Center, Center),
            "top-right-corner" => new(rightX, 0, marginRightPx, marginTopPx, Center, Center),
            "bottom-left-corner" => new(0, bottomY, marginLeftPx, marginBottomPx, Center, Center),
            "bottom-right-corner" => new(rightX, bottomY, marginRightPx, marginBottomPx, Center, Center),

            // Top edge band — vary horizontal alignment by name.
            "top-left" => new(marginLeftPx, 0, contentWidth, marginTopPx, Start, Center),
            "top-center" => new(marginLeftPx, 0, contentWidth, marginTopPx, Center, Center),
            "top-right" => new(marginLeftPx, 0, contentWidth, marginTopPx, End, Center),

            // Bottom edge band.
            "bottom-left" => new(marginLeftPx, bottomY, contentWidth, marginBottomPx, Start, Center),
            "bottom-center" => new(marginLeftPx, bottomY, contentWidth, marginBottomPx, Center, Center),
            "bottom-right" => new(marginLeftPx, bottomY, contentWidth, marginBottomPx, End, Center),

            // Left edge column — vary vertical placement by name; center horizontally in the margin.
            "left-top" => new(0, marginTopPx, marginLeftPx, contentHeight, Center, Start),
            "left-middle" => new(0, marginTopPx, marginLeftPx, contentHeight, Center, Center),
            "left-bottom" => new(0, marginTopPx, marginLeftPx, contentHeight, Center, End),

            // Right edge column.
            "right-top" => new(rightX, marginTopPx, marginRightPx, contentHeight, Center, Start),
            "right-middle" => new(rightX, marginTopPx, marginRightPx, contentHeight, Center, Center),
            "right-bottom" => new(rightX, marginTopPx, marginRightPx, contentHeight, Center, End),

            _ => null,
        };
        region = r ?? default;
        return r is not null;
    }
}
