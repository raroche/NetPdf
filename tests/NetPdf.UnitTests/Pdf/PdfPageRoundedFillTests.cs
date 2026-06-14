// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Unit tests for <see cref="PdfPage"/>'s rounded-rectangle operators (border-radius cycles) — the
/// uniform + per-corner fill, the rounded border stroke, and the rounded clip. Inspects the raw
/// (uncompressed) content-stream operators.
/// </summary>
public sealed class PdfPageRoundedFillTests
{
    private static string ContentOf(PdfPage page)
    {
        var (_, content) = page.Finalize();
        return Encoding.ASCII.GetString(content);
    }

    [Fact]
    public void FillRoundedRectangle_emits_a_bezier_path_fill()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, 8, 0.2, 0.4, 0.8);

        var content = ContentOf(page);
        Assert.Contains("0.2 0.4 0.8 rg", content);
        Assert.Contains(" m ", content);                       // path start
        Assert.Equal(4, CountOccurrences(content, " c "));     // one Bézier per corner
        Assert.Contains(" f Q", content);                      // filled + state restored
        Assert.DoesNotContain(" re ", content);                // not the square fast path
        // The path starts at the bottom edge's left arc-end: x+radius = 18.
        Assert.Contains("18 20 m", content);
    }

    [Fact]
    public void FillRoundedRectangle_with_non_positive_radius_delegates_to_the_square_fill()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, 0, 0.2, 0.4, 0.8);

        var content = ContentOf(page);
        Assert.Contains("10 20 100 50 re f Q", content);
        Assert.DoesNotContain(" c ", content);
    }

    [Fact]
    public void FillRoundedRectangle_clamps_the_radius_to_half_the_smaller_dimension()
    {
        // radius 999 on a 100×50 rect clamps to 25 — the path's first point is x+25 = 35
        // (a capsule, per the CSS B&B §5.5 overlap rule for the uniform case).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, 999, 0, 0, 0);

        Assert.Contains("35 20 m", ContentOf(page));
    }

    [Fact]
    public void FillRoundedRectangle_zero_area_is_a_no_op()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 0, 50, 8, 0, 0, 0);

        Assert.DoesNotContain(" f", ContentOf(page));
    }

    // ============================================================
    // Per-corner elliptical radii (border-radius-completion cycle)
    // ============================================================

    [Fact]
    public void FillRoundedRectangle_per_corner_emits_distinct_corner_arcs()
    {
        // Distinct circular radii per corner (TL=4, TR=8, BR=12, BL=16): four Bézier arcs, the path
        // starting at the bottom edge right of the BL corner (x + BL = 10 + 16 = 26).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50,
            new CornerRadii(4, 4, 8, 8, 12, 12, 16, 16), 0.2, 0.4, 0.8);

        var content = ContentOf(page);
        Assert.Contains("0.2 0.4 0.8 rg", content);
        Assert.Contains("26 20 m", content);                   // start = x + BL radius
        Assert.Contains("98 20 l", content);                   // bottom edge → left of BR (x+w-BR = 98)
        Assert.Equal(4, CountOccurrences(content, " c "));      // one Bézier per corner
        Assert.Contains(" f Q", content);
        Assert.DoesNotContain(" re ", content);                // not the square fast path
    }

    [Fact]
    public void FillRoundedRectangle_per_corner_all_zero_delegates_to_the_square_fill()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(10, 20, 100, 50, default(CornerRadii), 0.2, 0.4, 0.8);

        var content = ContentOf(page);
        Assert.Contains("10 20 100 50 re f Q", content);
        Assert.DoesNotContain(" c ", content);
    }

    [Fact]
    public void FillRoundedRectangle_percentage_ellipse_uses_distinct_horizontal_and_vertical_radii()
    {
        // `border-radius: 50%` on a 100×60 box → rx = 50, ry = 30 (an ellipse): the path starts at
        // x + rx = 50 and the bottom-right corner ends at y + ry = 30 (different from a circle).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangle(0, 0, 100, 60,
            new CornerRadii(50, 30, 50, 30, 50, 30, 50, 30), 0, 0, 0);

        var content = ContentOf(page);
        Assert.Contains("50 0 m", content);     // start = x + rx
        Assert.Contains("100 30 c", content);   // BR corner ends at (x+w, y+ry)
        Assert.Equal(4, CountOccurrences(content, " c "));
    }

    [Fact]
    public void FillRoundedRectangleRing_fills_the_annulus_with_even_odd()
    {
        // A uniform rounded border = the ring between the border box (radii 8) and the padding box
        // (inset 4 per side, radii 8−4 = 4): two rounded subpaths (8 Béziers) filled even-odd (`f*`)
        // with the border colour (a FILL, /ca — never a stroke /CA).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangleRing(
            10, 20, 100, 50, CornerRadii.Uniform(8),
            14, 24, 92, 42, CornerRadii.Uniform(4), 0, 0, 0);

        var content = ContentOf(page);
        Assert.Contains("0 0 0 rg", content);                  // border colour, FILL (rg) not stroke (RG)
        Assert.Equal(8, CountOccurrences(content, " c "));     // outer + inner each = 4 corner arcs
        Assert.Contains(" f* Q", content);                     // even-odd fill = the ring
        Assert.DoesNotContain(" S", content);                  // never stroked (no /CA pitfall)
    }

    [Fact]
    public void FillRoundedRectangleRing_small_radius_thick_border_keeps_the_outer_rounding()
    {
        // border-radius 2 + a 4px border: the OUTER path still rounds at 2 (the ring's outer corner is
        // exact for any border width — a centerline stroke would lose it), while the inner corner goes
        // sharp (max(0, 2−4) = 0 → a plain `re` inner subpath). post-PR-#172 review P2.
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangleRing(
            10, 20, 100, 50, CornerRadii.Uniform(2),
            14, 24, 92, 42, default, 0, 0, 0);

        var content = ContentOf(page);
        Assert.Equal(4, CountOccurrences(content, " c "));     // ONLY the outer rounds (inner is square)
        Assert.Contains(" re ", content);                      // the sharp inner subpath
        Assert.Contains(" f* Q", content);
    }

    [Fact]
    public void FillRoundedRectangleRing_uses_fill_alpha_via_extgstate_not_a_stroke()
    {
        // A semi-transparent ring selects an ExtGState (gs) for the alpha and FILLS (f*) — never a stroke
        // (no RG/S, so no /CA pitfall). The constant alpha lives as /ca in the ExtGState dict (its value
        // is asserted at the facade level on the full PDF; post-PR-#172 review P1).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangleRing(
            10, 20, 100, 50, CornerRadii.Uniform(8),
            14, 24, 92, 42, CornerRadii.Uniform(4), 0, 0, 0, 0.501961);

        var content = ContentOf(page);
        Assert.Contains(" gs ", content);                      // an ExtGState (constant alpha) is selected
        Assert.Contains(" f* Q", content);                     // ... for a FILL
        Assert.DoesNotContain(" S", content);                  // never a stroke
        Assert.DoesNotContain(" RG", content);                 // ... and no stroke colour
    }

    [Fact]
    public void FillRoundedRectangleRing_degenerate_inner_fills_a_solid_rounded_rect()
    {
        // A border ≥ half the box collapses the inner box (≤ 0) → the ring fills the WHOLE outer rounded
        // rect (plain `f`, no cut-out, no `f*`).
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.FillRoundedRectangleRing(
            10, 20, 40, 40, CornerRadii.Uniform(8),
            30, 40, 0, 0, default, 0, 0, 0);

        var content = ContentOf(page);
        Assert.Contains(" f Q", content);
        Assert.DoesNotContain(" f* ", content);
        Assert.Equal(4, CountOccurrences(content, " c "));     // outer only
    }

    [Fact]
    public void BeginRoundedRectangleClip_emits_a_rounded_clip_path()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.BeginRoundedRectangleClip(10, 20, 100, 50, CornerRadii.Uniform(8));
        page.RestoreGraphicsState();

        var content = ContentOf(page);
        Assert.Contains(" m ", content);
        Assert.Equal(4, CountOccurrences(content, " c "));
        Assert.Contains(" W n", content);                      // clip from the path
        Assert.DoesNotContain(" re W n", content);             // not the rectangular clip
    }

    [Fact]
    public void BeginRoundedRectangleClip_all_zero_falls_back_to_the_rectangular_clip()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);

        page.BeginRoundedRectangleClip(10, 20, 100, 50, default);
        page.RestoreGraphicsState();

        var content = ContentOf(page);
        Assert.Contains("10 20 100 50 re W n", content);
        Assert.DoesNotContain(" c ", content);
    }

    [Fact]
    public void CornerRadii_NormalizedFor_clamps_negatives_and_scales_overlap()
    {
        // Negatives clamp to 0; uniform 40 on a 50×50 box overlaps (40+40 > 50 on every edge) →
        // scaled by f = 50/80 = 0.625 → 25 everywhere (a circle inscribed, per §4.2).
        var n = CornerRadii.Uniform(40).NormalizedFor(50, 50);
        Assert.Equal(25, n.TopLeftX, 5);
        Assert.Equal(25, n.BottomRightY, 5);

        var withNeg = new CornerRadii(-5, -5, 10, 10, 0, 0, 0, 0).NormalizedFor(100, 100);
        Assert.Equal(0, withNeg.TopLeftX, 5);     // negative → 0
        Assert.Equal(10, withNeg.TopRightX, 5);   // within extents → unscaled

        Assert.False(CornerRadii.Uniform(8).NormalizedFor(0, 50).AnyPositive);   // degenerate → all 0
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
