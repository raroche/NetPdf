// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Unit tests for <see cref="PageMarginBoxGeometry"/> — Phase 3 Task 21 cycle 3, mapping each of
/// the 16 CSS Page 3 §6.4 margin-box names to its page-px rectangle + alignment factors.
/// </summary>
public sealed class PageMarginBoxGeometryTests
{
    // Page 1000 × 2000 px; margins top 100, right 50, bottom 80, left 40.
    //   contentWidth = 1000 − 40 − 50 = 910; contentHeight = 2000 − 100 − 80 = 1820.
    //   rightX = 950; bottomY = 1920.
    private const double PageW = 1000, PageH = 2000, MTop = 100, MRight = 50, MBottom = 80, MLeft = 40;

    [Theory]
    // name, X, Y, Width, Height, HAlign, VAlign
    [InlineData("top-center", 40, 0, 910, 100, 0.5, 0.5)]
    [InlineData("top-left", 40, 0, 910, 100, 0.0, 0.5)]
    [InlineData("top-right", 40, 0, 910, 100, 1.0, 0.5)]
    [InlineData("bottom-center", 40, 1920, 910, 80, 0.5, 0.5)]
    [InlineData("bottom-left", 40, 1920, 910, 80, 0.0, 0.5)]
    [InlineData("top-left-corner", 0, 0, 40, 100, 0.5, 0.5)]
    [InlineData("top-right-corner", 950, 0, 50, 100, 0.5, 0.5)]
    [InlineData("bottom-right-corner", 950, 1920, 50, 80, 0.5, 0.5)]
    [InlineData("left-top", 0, 100, 40, 1820, 0.5, 0.0)]
    [InlineData("left-middle", 0, 100, 40, 1820, 0.5, 0.5)]
    [InlineData("left-bottom", 0, 100, 40, 1820, 0.5, 1.0)]
    [InlineData("right-top", 950, 100, 50, 1820, 0.5, 0.0)]
    [InlineData("right-middle", 950, 100, 50, 1820, 0.5, 0.5)]
    [InlineData("right-bottom", 950, 100, 50, 1820, 0.5, 1.0)]
    [InlineData("bottom-right", 40, 1920, 910, 80, 1.0, 0.5)]
    [InlineData("bottom-left-corner", 0, 1920, 40, 80, 0.5, 0.5)]
    public void TryGetRegion_maps_names_to_rects_and_alignment(
        string name, double x, double y, double w, double h, double hAlign, double vAlign)
    {
        Assert.True(PageMarginBoxGeometry.TryGetRegion(name, PageW, PageH, MTop, MRight, MBottom, MLeft, out var r));
        Assert.Equal(x, r.X, 3);
        Assert.Equal(y, r.Y, 3);
        Assert.Equal(w, r.Width, 3);
        Assert.Equal(h, r.Height, 3);
        Assert.Equal(hAlign, r.HAlign, 3);
        Assert.Equal(vAlign, r.VAlign, 3);
    }

    [Fact]
    public void TryGetRegion_assigns_the_variable_axis()
    {
        // The §5.3 variable axis: top/bottom edges shrink WIDTH, left/right shrink HEIGHT, corners
        // neither. (An internal-enum theory param can't sit on a public xUnit method, so assert inline.)
        PageMarginBoxGeometry.MarginBoxAxis Axis(string name)
        {
            Assert.True(PageMarginBoxGeometry.TryGetRegion(name, PageW, PageH, MTop, MRight, MBottom, MLeft, out var r));
            return r.VariableAxis;
        }
        Assert.Equal(PageMarginBoxGeometry.MarginBoxAxis.Horizontal, Axis("top-center"));
        Assert.Equal(PageMarginBoxGeometry.MarginBoxAxis.Horizontal, Axis("bottom-left"));
        Assert.Equal(PageMarginBoxGeometry.MarginBoxAxis.Vertical, Axis("left-middle"));
        Assert.Equal(PageMarginBoxGeometry.MarginBoxAxis.Vertical, Axis("right-bottom"));
        Assert.Equal(PageMarginBoxGeometry.MarginBoxAxis.None, Axis("top-left-corner"));
        Assert.Equal(PageMarginBoxGeometry.MarginBoxAxis.None, Axis("bottom-right-corner"));
    }

    [Theory]
    [InlineData("middle-center")]   // not a CSS Page 3 §6.4 box
    [InlineData("")]
    [InlineData("TOP-CENTER")]      // resolver lowercases names before lookup; this helper is exact
    public void TryGetRegion_rejects_unknown_names(string name)
    {
        Assert.False(PageMarginBoxGeometry.TryGetRegion(name, PageW, PageH, MTop, MRight, MBottom, MLeft, out _));
    }

    [Fact]
    public void TryGetRegion_clamps_bands_to_non_negative_when_margins_exceed_the_page()
    {
        // Margins exceeding the page would make a content band negative; it clamps to 0 so no
        // negative region size reaches the painter (→ non-finite PDF coords). Mirrors the body
        // content-box clamp (review thread on PageMarginBoxGeometry).
        Assert.True(PageMarginBoxGeometry.TryGetRegion("top-center", 100, 100, 30, 60, 30, 60, out var top));
        Assert.Equal(0, top.Width, 3);    // contentWidth = max(0, 100 − 60 − 60) = 0
        Assert.True(PageMarginBoxGeometry.TryGetRegion("left-middle", 100, 100, 60, 30, 60, 30, out var left));
        Assert.Equal(0, left.Height, 3);  // contentHeight = max(0, 100 − 60 − 60) = 0
    }

    // ---- §5.3 sibling-box overlap resolution (Task 21) ----

    private static PageMarginBoxGeometry.EdgeTriple Triple(
        double a, bool hasA, double b, bool hasB, double c, bool hasC) => new(a, hasA, b, hasB, c, hasC);

    [Fact]
    public void ResolveEdgeOverlap_returns_unchanged_when_boxes_dont_overlap()
    {
        // A(100)|B(200)|C(100) in a 1000 band: A [0,100], B centered [400,600], C [900,1000] — no
        // overlap, so each keeps its desired size + role position (the per-box cycle-14/15 model).
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(100, true, 200, true, 100, true), 1000);
        Assert.Equal(100, r.SizeA, 3); Assert.Equal(0, r.StartA, 3);
        Assert.Equal(200, r.SizeB, 3); Assert.Equal(400, r.StartB, 3);
        Assert.Equal(100, r.SizeC, 3); Assert.Equal(900, r.StartC, 3);
    }

    [Fact]
    public void ResolveEdgeOverlap_clamps_a_wide_start_box_to_the_center_gap()
    {
        // A wants 500 but B(200) is centered at [400,600]; A clamps to the left gap [0,400], B stays put.
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(500, true, 200, true, 0, false), 1000);
        Assert.Equal(400, r.SizeA, 3); Assert.Equal(0, r.StartA, 3);
        Assert.Equal(200, r.SizeB, 3); Assert.Equal(400, r.StartB, 3);   // B centered, unmoved
    }

    [Fact]
    public void ResolveEdgeOverlap_clamps_a_wide_end_box_to_the_center_gap()
    {
        // C wants 500 but B(200) is centered at [400,600]; C clamps to the right gap [600,1000].
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(0, false, 200, true, 500, true), 1000);
        Assert.Equal(400, r.SizeC, 3); Assert.Equal(600, r.StartC, 3);
        Assert.Equal(200, r.SizeB, 3); Assert.Equal(400, r.StartB, 3);
    }

    [Fact]
    public void ResolveEdgeOverlap_shrinks_two_side_boxes_proportionally_without_a_center()
    {
        // No center box: A(700)+C(600)=1300 > 1000 → proportional shrink, tiling the band edge-to-edge.
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(700, true, 0, false, 600, true), 1000);
        Assert.Equal(700.0 * 1000 / 1300, r.SizeA, 2); Assert.Equal(0, r.StartA, 3);
        Assert.Equal(600.0 * 1000 / 1300, r.SizeC, 2);
        Assert.Equal(1000 - 600.0 * 1000 / 1300, r.StartC, 2);   // A and C abut, no gap
    }

    [Fact]
    public void ResolveEdgeOverlap_a_full_band_center_box_squeezes_the_sides_to_zero()
    {
        // B wants 1500 (clamped to the 1000 band) → fills it; A and C are squeezed to 0.
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(100, true, 1500, true, 100, true), 1000);
        Assert.Equal(1000, r.SizeB, 3);
        Assert.Equal(0, r.SizeA, 3);
        Assert.Equal(0, r.SizeC, 3);
    }

    [Fact]
    public void ResolveEdgeOverlap_single_box_is_unchanged()
    {
        // One box, no sibling → no overlap possible → its desired size + start are returned as-is.
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(500, true, 0, false, 0, false), 1000);
        Assert.Equal(500, r.SizeA, 3);
        Assert.Equal(0, r.StartA, 3);
    }
}
