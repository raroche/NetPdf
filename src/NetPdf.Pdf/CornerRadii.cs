// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Pdf;

/// <summary>The four <c>border-radius</c> corners of a box, each an ELLIPTICAL radius
/// (a horizontal + a vertical component, CSS Backgrounds &amp; Borders 3 §4.1) in PDF points.
/// A circular corner has <c>X == Y</c>; a percentage <c>border-radius</c> on a non-square box
/// resolves to <c>X != Y</c> (an ellipse). Corners are named in CSS terms (top-left … etc.);
/// <see cref="PdfPage"/> maps them to its bottom-left-origin coordinate space when it builds the
/// path. The explicit two-radii-per-corner <c>Rx / Ry</c> spelling is recovered upstream
/// (border-radius-elliptical cycle): AngleSharp drops the slash form, so <c>BorderRadiusShorthandExpander</c>
/// expands its vertical radii onto the internal <c>-netpdf-border-{corner}-radius-y</c> longhands, which
/// <c>FragmentPainter.ReadCornerRadii</c> reads as each corner's vertical component.</summary>
public readonly record struct CornerRadii(
    double TopLeftX, double TopLeftY,
    double TopRightX, double TopRightY,
    double BottomRightX, double BottomRightY,
    double BottomLeftX, double BottomLeftY)
{
    /// <summary>A uniform circular radius on all four corners (the common
    /// <c>border-radius: &lt;length&gt;</c> case).</summary>
    public static CornerRadii Uniform(double radius) =>
        new(radius, radius, radius, radius, radius, radius, radius, radius);

    /// <summary>Whether any corner rounds at all (else the box is a plain rectangle and the
    /// caller should take the square fast path).</summary>
    public bool AnyPositive =>
        TopLeftX > 0 || TopLeftY > 0 || TopRightX > 0 || TopRightY > 0
        || BottomRightX > 0 || BottomRightY > 0 || BottomLeftX > 0 || BottomLeftY > 0;

    /// <summary>Whether all four corners are the SAME circular (X == Y) radius — lets a caller route
    /// to the byte-stable uniform <see cref="PdfPage.FillRoundedRectangle(double,double,double,double,double,double,double,double,double)"/>
    /// path. <paramref name="radius"/> is that shared value (0 when not uniform).</summary>
    public bool IsUniformCircular(out double radius)
    {
        radius = TopLeftX;
        return TopLeftX == TopLeftY
            && TopLeftX == TopRightX && TopRightX == TopRightY
            && TopLeftX == BottomRightX && BottomRightX == BottomRightY
            && TopLeftX == BottomLeftX && BottomLeftX == BottomLeftY;
    }

    /// <summary>This set with each component clamped to a finite, non-negative value, then scaled by
    /// the single §4.2 overlap factor <c>f</c> = min over the four edges of edge-length ÷ the sum of
    /// the two radii on that edge (capped at 1) so adjacent corners never overlap. Top/bottom edges
    /// constrain the HORIZONTAL radii, left/right edges the VERTICAL radii (CSS B&amp;B 3 §4.2). A
    /// degenerate <paramref name="width"/>/<paramref name="height"/> (≤ 0) yields all-zero.</summary>
    public CornerRadii NormalizedFor(double width, double height)
    {
        if (!(width > 0) || !(height > 0)) return default;
        static double C(double v) => double.IsFinite(v) && v > 0 ? v : 0;
        var tlx = C(TopLeftX); var tly = C(TopLeftY);
        var trx = C(TopRightX); var trY = C(TopRightY);
        var brx = C(BottomRightX); var bry = C(BottomRightY);
        var blx = C(BottomLeftX); var bly = C(BottomLeftY);

        var f = 1.0;
        void Limit(double sum, double extent) { if (sum > 0) f = Math.Min(f, extent / sum); }
        Limit(tlx + trx, width);   // top edge
        Limit(blx + brx, width);   // bottom edge
        Limit(tly + bly, height);  // left edge
        Limit(trY + bry, height);  // right edge

        return new CornerRadii(
            tlx * f, tly * f, trx * f, trY * f,
            brx * f, bry * f, blx * f, bly * f);
    }
}
