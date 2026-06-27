// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 border-image (PR 4) — end-to-end: a decoded <c>border-image</c> slices its source into
/// the 9 border regions (each a clipped image placement, <c>re W n … cm … Do</c>) and REPLACES the normal
/// border rendering. Page content is uncompressed, so the operators are string-inspectable.</summary>
public sealed class BorderImagePaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string DataUri() =>
        "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(90, 90));

    private static string Html(string borderImage) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:120px;height:120px;border:30px solid #000;border-image:{borderImage}\"></div>" +
        "</body></html>";

    [Fact]
    public void Border_image_slices_the_source_and_replaces_the_border()
    {
        var text = Latin1(HtmlPdf.Convert(Html($"url({DataUri()}) 30 fill")));
        Assert.Contains("Do", text);                          // image slices placed
        Assert.Contains("re\nW n", text.Replace(" W n", "\nW n")); // each slice clips to its dest rect
        Assert.DoesNotContain("0 0 0 rg", text);              // the solid black border is NOT painted
    }

    [Fact]
    public void Non_stretch_repeat_now_tiles_without_a_diagnostic()
    {
        // border-image completion — round/repeat/space now tile the edges; no approximation diagnostic.
        var result = HtmlPdf.ConvertDetailed(Html($"url({DataUri()}) 30 round"));
        Assert.True(Count(Latin1(result.Pdf), " Do ") > 8);   // edges tiled (more than the 8 stretch slices)
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBorderImageUnsupported001);
    }

    [Fact]
    public void No_border_image_falls_back_to_the_normal_border()
    {
        // No border-image → the solid black border still fills.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:120px;height:120px;border:30px solid #000\"></div>" +
            "</body></html>"));
        Assert.Contains("0 0 0 rg", text);                    // the normal border paints
    }

    // ---- PR-229 review fixes ----

    [Fact]
    public void Later_source_none_overrides_earlier_shorthand_by_source_order()
    {
        // [P2] cascade order: `border-image: url(...)` then a LATER `border-image-source: none` → no
        // border-image (the normal solid border paints). Pre-fix the longhand always won regardless of order.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:120px;height:120px;border:30px solid #000;border-image:url({DataUri()}) 30 fill;border-image-source:none\"></div>" +
            "</body></html>"));
        Assert.Contains("0 0 0 rg", text);                    // border-image overridden → solid border paints
    }

    [Fact]
    public void Later_shorthand_overrides_earlier_source_none_by_source_order()
    {
        // The reverse: `border-image-source: none` then a LATER `border-image: url(...)` → the image paints.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:120px;height:120px;border:30px solid #000;border-image-source:none;border-image:url({DataUri()}) 30 fill\"></div>" +
            "</body></html>"));
        Assert.Contains("Do", text);                          // border-image wins
        Assert.DoesNotContain("0 0 0 rg", text);
    }

    [Fact]
    public void Border_image_paints_even_with_print_backgrounds_false()
    {
        // [P2] border-image paints the BORDER area, which renders regardless of PrintBackgrounds (like a
        // normal border) — not gated like background-image.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:120px;height:120px;border:30px solid transparent;border-image:url({DataUri()}) 30 fill\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { PrintBackgrounds = false }));
        Assert.Contains("Do", text);
    }

    [Fact]
    public void Width_and_outset_are_now_honored_without_a_diagnostic()
    {
        // border-image completion — border-image-width / -outset are applied, not diagnosed as ignored.
        var result = HtmlPdf.ConvertDetailed(Html($"url({DataUri()}) 30 / 10px / 5px"));
        Assert.Contains("Do", Latin1(result.Pdf));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBorderImageUnsupported001);
    }

    // ---- border-image completion: edge tiling + width + outset ----

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Render a 120×120 box with a 30px border + border-image longhands (90×90 source). With the
    /// default slice 30 (→ ⅓) the stretch baseline is exactly 8 placements (4 corners + 4 edges).</summary>
    private static string RenderLong(string slice, string repeat, string extra = "") =>
        Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:120px;height:120px;border:30px solid #000;" +
            $"border-image-source:url({DataUri()});border-image-slice:{slice};" +
            $"border-image-repeat:{repeat};{extra}\"></div></body></html>"));

    [Fact]
    public void Stretch_places_exactly_eight_slices()
    {
        Assert.Equal(8, Count(RenderLong("30", "stretch"), " Do "));   // 4 corners + 4 stretched edges
    }

    [Fact]
    public void Repeat_tiles_the_edges_and_adds_centering_clips()
    {
        var repeat = RenderLong("30", "repeat");
        var round = RenderLong("30", "round");
        Assert.True(Count(repeat, " Do ") > 8);                        // edges tiled
        // repeat centers + clips each tiled edge → extra "re W n" beyond its own per-slice clips; round
        // fits exactly and adds none. Compare the clip-minus-placement delta (robust to other page clips).
        Assert.True(Count(repeat, "re W n") - Count(repeat, " Do ")
                  > Count(round, "re W n") - Count(round, " Do "));
    }

    [Fact]
    public void Round_tiles_the_edges_with_no_centering_clip()
    {
        var round = RenderLong("30", "round");
        var stretch = RenderLong("30", "stretch");
        Assert.True(Count(round, " Do ") > 8);                         // edges tiled
        // round fits an exact whole number of tiles → no extra clip beyond the per-slice ones (same delta
        // as stretch, which also adds none).
        Assert.Equal(Count(stretch, "re W n") - Count(stretch, " Do "),
                     Count(round, "re W n") - Count(round, " Do "));
    }

    [Fact]
    public void Space_tiles_and_drops_edges_when_no_whole_tile_fits()
    {
        Assert.True(Count(RenderLong("30", "space"), " Do ") > 8);     // normal: edges tiled with gaps
        // A 10px slice makes the natural tile larger than the 120px edge → not one whole tile fits → the
        // edges paint nothing (CSS B&B §6.3), leaving only the 4 corners.
        Assert.Equal(4, Count(RenderLong("10", "space"), " Do "));
    }

    [Fact]
    public void Border_image_width_zero_collapses_the_border_image()
    {
        Assert.Equal(0, Count(RenderLong("30", "stretch", "border-image-width:0;"), " Do "));
    }

    [Fact]
    public void Border_image_width_changes_the_geometry()
    {
        var def = RenderLong("30", "stretch");
        var wide = RenderLong("30", "stretch", "border-image-width:50px;");
        Assert.Equal(8, Count(wide, " Do "));                          // still 8 stretched slices…
        Assert.NotEqual(def, wide);                                    // …with different dest thicknesses
    }

    [Fact]
    public void Border_image_outset_extends_the_area()
    {
        var def = RenderLong("30", "stretch");
        var outset = RenderLong("30", "stretch", "border-image-outset:20px;");
        Assert.Equal(8, Count(outset, " Do "));                        // same 8 slices, shifted outward
        Assert.NotEqual(def, outset);                                  // grown area moves the placements
    }

    // ---- numeric geometry (PR-232 review [P1]/[P2]/[P4]) ----

    private const double PtPerPx = 72.0 / 96.0;   // PdfUnits.PointsPerPixel

    /// <summary>Parse the DEST rect (px → pt) of every placed border-image slice from the page content.
    /// Each <c>PlaceImageSlice</c> emits <c>q dx dy dw dh re W n a 0 0 d e f cm /Im Do Q</c>; the trailing
    /// <c>a 0 0</c> (the cm matrix) distinguishes a slice from a plain <c>BeginRectangleClip</c> (which is
    /// followed by another <c>q</c>), so only real placements match.</summary>
    private static List<(double X, double Y, double W, double H)> SliceRects(string pdf)
    {
        var rx = new Regex(@"q (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re W n -?[\d.]+ 0 0 ");
        double P(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return rx.Matches(pdf).Select(m => (P(m, 1), P(m, 2), P(m, 3), P(m, 4))).ToList();
    }

    /// <summary>Render a wPx×hPx box with explicit border-image longhands (90×90 source). borderCss sets the
    /// CSS border (e.g. <c>border:30px solid #000;</c> or <c>border:0 solid transparent;</c>).</summary>
    private static string RenderGeom(int wPx, int hPx, string borderCss, string slice, string repeat, string extra) =>
        Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:{wPx}px;height:{hPx}px;{borderCss}" +
            $"border-image-source:url({DataUri()});border-image-slice:{slice};" +
            $"border-image-repeat:{repeat};{extra}\"></div></body></html>"));

    [Fact]
    public void Width_overlap_uses_a_single_global_factor_keeping_corners_square()
    {
        // [P1] 200×40 area, no CSS border, border-image-width:30px on all four. Horizontal sum (60) fits the
        // 200 area; vertical sum (60) overflows the 40 area. The spec's SINGLE global factor
        // f = min(200/60, 40/60, 1) = 0.667 shrinks ALL four to 20px → 15pt SQUARE corners. A per-axis clamp
        // would shrink only top/bottom (→ 30×20px = 22.5×15pt, NOT square).
        var rects = SliceRects(RenderGeom(200, 40, "border:0 solid transparent;", "30", "stretch",
            "border-image-width:30px;"));
        Assert.NotEmpty(rects);
        var squares = rects.Count(r => Math.Abs(r.W - r.H) < 0.5);
        Assert.True(squares >= 4, $"expected ≥4 square corners (global reduction), got {squares}");
        // The square corners measure 20px = 15pt on a side.
        Assert.Contains(rects, r => Math.Abs(r.W - 15.0) < 0.5 && Math.Abs(r.H - 15.0) < 0.5);
    }

    [Fact]
    public void Zero_border_with_explicit_width_and_outset_still_paints()
    {
        // [P2] a box with zero CSS borders but an explicit border-image-width paints (the old early-return on
        // CSS border widths dropped this). The corners take the explicit 20px = 15pt width.
        var rects = SliceRects(RenderGeom(120, 120, "border:0 solid transparent;", "30", "stretch",
            "border-image-width:20px;border-image-outset:5px;"));
        Assert.NotEmpty(rects);
        Assert.Contains(rects, r => Math.Abs(r.W - 15.0) < 0.5 && Math.Abs(r.H - 15.0) < 0.5);
    }

    [Fact]
    public void Outset_expands_the_area_bounding_box_by_the_outset()
    {
        var def = SliceRects(RenderGeom(120, 120, "border:30px solid #000;", "30", "stretch", ""));
        var outset = SliceRects(RenderGeom(120, 120, "border:30px solid #000;", "30", "stretch", "border-image-outset:20px;"));
        double SpanX(List<(double X, double Y, double W, double H)> r) => r.Max(b => b.X + b.W) - r.Min(b => b.X);
        double SpanY(List<(double X, double Y, double W, double H)> r) => r.Max(b => b.Y + b.H) - r.Min(b => b.Y);
        // The area grows 20px on every side → its bbox grows 40px = 30pt in each dimension.
        Assert.Equal(30.0, SpanX(outset) - SpanX(def), precision: 1);
        Assert.Equal(30.0, SpanY(outset) - SpanY(def), precision: 1);
    }

    [Fact]
    public void Width_sets_the_corner_band_thickness()
    {
        var def = SliceRects(RenderGeom(120, 120, "border:30px solid #000;", "30", "stretch", ""));
        var wide = SliceRects(RenderGeom(120, 120, "border:30px solid #000;", "30", "stretch", "border-image-width:50px;"));
        // Default corner = the 30px border = 22.5pt square; with width:50px → 37.5pt square.
        Assert.Contains(def, r => Math.Abs(r.W - 22.5) < 0.5 && Math.Abs(r.H - 22.5) < 0.5);
        Assert.Contains(wide, r => Math.Abs(r.W - 37.5) < 0.5 && Math.Abs(r.H - 37.5) < 0.5);
    }

    [Fact]
    public void Round_scales_tiles_to_fit_while_space_keeps_natural_size_with_gaps()
    {
        // Asymmetric slices: top/bottom slice 20px, left/right slice 30px (on a 90px image). The top edge's
        // natural tile = srcMid(30px) × (wt 30px / srcTop 20px) = 45px; innerW = 180−30−30 = 120px.
        //   round → n = round(120/45) = 3 tiles SCALED to 40px = 30pt (they exactly fill 120px).
        //   space → n = floor(120/45) = 2 tiles at the NATURAL 45px = 33.75pt, with gaps.
        var round = SliceRects(RenderGeom(120, 120, "border:30px solid #000;", "20 30", "round", ""));
        var space = SliceRects(RenderGeom(120, 120, "border:30px solid #000;", "20 30", "space", ""));
        var topBandHeight = 22.5; // wt = 30px border = 22.5pt
        int CountW(List<(double X, double Y, double W, double H)> r, double w) =>
            r.Count(b => Math.Abs(b.H - topBandHeight) < 0.5 && Math.Abs(b.W - w) < 0.5);
        Assert.True(CountW(round, 30.0) >= 3, "round should scale top tiles to 30pt (40px) and fill the edge");
        Assert.Equal(0, CountW(round, 33.75));            // round never keeps the natural 45px size
        Assert.True(CountW(space, 33.75) >= 2, "space should keep the natural 33.75pt (45px) tiles");
        Assert.Equal(0, CountW(space, 30.0));             // space never scales to 40px
    }

    [Fact]
    public void Gradient_source_is_diagnosed_and_not_painted()
    {
        // [P3] a non-url() (gradient) border-image-source is unsupported → diagnosed, normal border paints.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:120px;height:120px;border:30px solid #000;border-image:linear-gradient(red,blue) 30\"></div>" +
            "</body></html>");
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBorderImageUnsupported001);
        Assert.Contains("0 0 0 rg", Latin1(result.Pdf));      // no border-image → solid border paints
    }
}
