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

    // RIGID boxes by default (min == desired) → the center-priority CLAMP path, so these legacy cases
    // keep their cycle-16 assertions. The min-content FLEX is exercised by FlexTriple below.
    private static PageMarginBoxGeometry.EdgeTriple Triple(
        double a, bool hasA, double b, bool hasB, double c, bool hasC) =>
        new(a, hasA, b, hasB, c, hasC, MinA: a, MinB: b, MinC: c);

    /// <summary>An edge triple with explicit per-box (min, max) — for the min/max-content flex path. A
    /// <see langword="null"/> box is ABSENT (<c>Has… = false</c>), so a test for the no-center path isn't
    /// accidentally given a phantom <c>(0, 0)</c> center box that would route it through the center-box
    /// branch instead (Copilot review).</summary>
    private static PageMarginBoxGeometry.EdgeTriple FlexTriple(
        (double Min, double Max)? a, (double Min, double Max)? b, (double Min, double Max)? c) =>
        new(a?.Max ?? 0, a is not null, b?.Max ?? 0, b is not null, c?.Max ?? 0, c is not null,
            a?.Min ?? 0, b?.Min ?? 0, c?.Min ?? 0);

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
        // One box, no sibling → no overlap possible → its desired size + start are returned as-is, and
        // the ABSENT B/C boxes report (0, 0) per the contract (not the role-derived l/2, l offsets).
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(Triple(500, true, 0, false, 0, false), 1000);
        Assert.Equal(500, r.SizeA, 3);
        Assert.Equal(0, r.StartA, 3);
        Assert.Equal(0, r.SizeB, 3); Assert.Equal(0, r.StartB, 3);
        Assert.Equal(0, r.SizeC, 3); Assert.Equal(0, r.StartC, 3);
    }

    // ---- §5.3.2 min/max-content FLEX (wrappable content; Min < Desired) ----

    [Fact]
    public void ResolveEdgeOverlap_flex_distributes_between_min_and_max()
    {
        // Two over-constrained side boxes (no center), each (min 100, max 600), band 1000. They overlap
        // (600+600>1000) and CAN flex → linear interpolation: factor = (1000−200)/(1200−200) = 0.8 →
        // each gets 100 + 500×0.8 = 500 (A flush start [0,500], C flush end [500,1000]).
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(FlexTriple((100, 600), null, (100, 600)), 1000);
        Assert.Equal(500, r.SizeA, 2); Assert.Equal(0, r.StartA, 3);
        Assert.Equal(500, r.SizeC, 2); Assert.Equal(500, r.StartC, 2);
    }

    [Fact]
    public void ResolveEdgeOverlap_flex_keeps_the_center_box_centered_against_the_imaginary_AC_box()
    {
        // P1 (was a TILING test): a wide side box must NOT push the center box off-centre. A rigid side
        // (min == max == 100) + a flexible center (min 100, max 900), band 1000. The center is flexed
        // against the imaginary AC box (2 × max(A, C) = 200), so it gets 1000 − 200 = 800 and stays
        // CENTERED at [100, 900] (centre = 500). A keeps its rigid 100 in the left gap [0, 100]; the
        // mirror-image right gap [900, 1000] is empty (no C). Previously this TILED B right after A.
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(FlexTriple((100, 100), (100, 900), null), 1000);
        Assert.Equal(100, r.SizeA, 3); Assert.Equal(0, r.StartA, 3);     // rigid side kept at its size
        Assert.Equal(800, r.SizeB, 3); Assert.Equal(100, r.StartB, 3);
        Assert.Equal(500, r.StartB + r.SizeB / 2.0, 3);                  // B's centre stays at the band centre
    }

    [Fact]
    public void ResolveEdgeOverlap_flex_three_boxes_keep_the_center_centered_and_dont_overlap()
    {
        // P1 (was a TILING test): A(100,500) | B(100,300) | C(100,200), band 1000. B is flexed against the
        // imaginary AC box (2 × max(500,200) = 1000) → B = 240, CENTERED at [380, 620] (centre 500). A and
        // C are then sized in the equal side gaps (380 each): A clamps to 380, C fits its 200. No overlap,
        // and B is centred — previously B was tiled to [500, 800], off-centre.
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(FlexTriple((100, 500), (100, 300), (100, 200)), 1000);
        Assert.Equal(380, r.SizeA, 3); Assert.Equal(0, r.StartA, 3);
        Assert.Equal(240, r.SizeB, 3); Assert.Equal(380, r.StartB, 3);
        Assert.Equal(500, r.StartB + r.SizeB / 2.0, 3);                  // centre invariance
        Assert.Equal(200, r.SizeC, 3); Assert.Equal(800, r.StartC, 3);
        Assert.True(r.StartA + r.SizeA <= r.StartB + 0.5, "A must not overlap B");
        Assert.True(r.StartB + r.SizeB <= r.StartC + 0.5, "B must not overlap C");
    }

    [Fact]
    public void ResolveEdgeOverlap_center_box_stays_full_size_and_centered_beside_a_wide_wrappable_side()
    {
        // P1 — the realistic running-header case: a rigid centred page number (min == max == 100) beside a
        // WIDE WRAPPABLE side header (min 50, max 800), band 1000. The wrappable side's min-content is
        // small, so the imaginary AC box (2 × max(50,0) = 100) is small → the centre keeps its full 100 and
        // stays CENTERED ([450, 550], centre 500); the side flexes to its 450-wide gap (and re-wraps).
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(FlexTriple((50, 800), (100, 100), null), 1000);
        Assert.Equal(100, r.SizeB, 3); Assert.Equal(450, r.StartB, 3);
        Assert.Equal(500, r.StartB + r.SizeB / 2.0, 3);                  // page number stays centred + full
        Assert.Equal(450, r.SizeA, 3); Assert.Equal(0, r.StartA, 3);    // side shrinks to its gap
        Assert.True(r.StartA + r.SizeA <= r.StartB + 0.5, "the side must not overlap the centre");
    }

    [Fact]
    public void ResolveEdgeOverlap_min_overflow_distributes_proportional_to_min_content()
    {
        // P2 — when the min-contents don't fit (no centre box), the band is distributed PROPORTIONALLY to
        // each box's MIN-content (not its max). A (min 400, max 500) + C (min 800, max 900), band 1000:
        // Σmin = 1200 > 1000 → A = 1000 × 400/1200 = 333.33, C = 1000 × 800/1200 = 666.67. (Proportional to
        // MAX would give 357 / 643 — different — so this asymmetric case proves it's min-proportional.)
        var r = PageMarginBoxGeometry.ResolveEdgeOverlap(FlexTriple((400, 500), null, (800, 900)), 1000);
        Assert.Equal(1000.0 * 400 / 1200, r.SizeA, 2); Assert.Equal(0, r.StartA, 3);
        Assert.Equal(1000.0 * 800 / 1200, r.SizeC, 2);
        Assert.Equal(1000 - 1000.0 * 800 / 1200, r.StartC, 2);          // C flush end, no overlap
        Assert.True(r.StartA + r.SizeA <= r.StartC + 0.5, "A must not overlap C");
    }
}
