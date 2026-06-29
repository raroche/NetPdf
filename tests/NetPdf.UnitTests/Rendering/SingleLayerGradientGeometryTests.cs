// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients — a SINGLE-LAYER gradient honors <c>background-origin</c> (the axis /
/// center / sweep spans the origin box, initial padding-box) and <c>background-clip</c> (the shading is
/// clipped to the clip box, initial border-box), matching the <c>url()</c> image path + multi-layer
/// gradient layers. A box with no border / padding is byte-identical to the old border-box behavior
/// (padding-box == border-box). Page content is uncompressed → operators are string-inspectable.</summary>
public sealed class SingleLayerGradientGeometryTests
{
    private const double PtPerPx = 0.75;
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // The axis /Coords of the (single) axial shading.
    private static (double X0, double Y0, double X1, double Y1) Coords(string pdf)
    {
        var m = Regex.Match(pdf, @"/Coords \[([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)\]");
        Assert.True(m.Success, "no /Coords found");
        double P(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return (P(1), P(2), P(3), P(4));
    }

    // The clip rect (w, h in pt) the shading is painted into: `x y w h re W n [<cm> cm] /Sh<n> sh`
    // (a radial ellipse inserts a CTM between `W n` and the `sh`).
    private static (double W, double H) ShadingClip(string pdf)
    {
        var m = Regex.Match(pdf, @"([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+) re W n (?:[-\d. ]+ cm )?/Sh\d+ sh");
        Assert.True(m.Success, "no shading clip rect found");
        return (double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    // The outer radius (r1) of the (single) radial shading: /Coords [cx0 cy0 r0 cx1 cy1 r1].
    private static double RadialOuterRadius(string pdf)
    {
        var m = Regex.Match(pdf, @"/Coords \[([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)\]");
        Assert.True(m.Success, "no radial /Coords found");
        return double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
    }

    // A SQUARE box → the radial ending shape is a circle (rx == ry), so no CTM and the outer radius is
    // the farthest-corner distance directly.
    private static string RadialHtml(string extraStyle) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:100px;{extraStyle}background-image:radial-gradient(red, blue)\"></div>" +
        "</body></html>";

    private static string Html(string extraStyle) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;{extraStyle}background-image:linear-gradient(to right, red, blue)\"></div>" +
        "</body></html>";

    [Fact]
    public void No_border_or_padding_renders_a_native_shading_spanning_the_box()
    {
        // padding-box == border-box here, so the default is byte-equivalent to the old behavior.
        var t = Latin1(HtmlPdf.Convert(Html("")));
        Assert.Contains("/ShadingType 2", t);
        var (x0, _, x1, _) = Coords(t);
        Assert.Equal(100 * PtPerPx, x1 - x0, 3); // `to right` axis spans the 100px box width
    }

    [Fact]
    public void Background_origin_border_box_spans_the_full_border_box()
    {
        // content 100px + padding 2×5 + border 2×10 = 130px border box; origin: border-box → axis = 130px.
        var t = Latin1(HtmlPdf.Convert(Html("border:10px solid black;padding:5px;background-origin:border-box;")));
        var (x0, _, x1, _) = Coords(t);
        Assert.Equal(130 * PtPerPx, x1 - x0, 3);
    }

    [Fact]
    public void Background_origin_defaults_to_padding_box()
    {
        // Default origin = padding-box: content 100 + padding 2×5 = 110px positioning area.
        var t = Latin1(HtmlPdf.Convert(Html("border:10px solid black;padding:5px;")));
        var (x0, _, x1, _) = Coords(t);
        Assert.Equal(110 * PtPerPx, x1 - x0, 3);
    }

    [Fact]
    public void Background_origin_content_box_spans_only_the_content_box()
    {
        var t = Latin1(HtmlPdf.Convert(Html("border:10px solid black;padding:5px;background-origin:content-box;")));
        var (x0, _, x1, _) = Coords(t);
        Assert.Equal(100 * PtPerPx, x1 - x0, 3); // content box = the 100px width
    }

    [Fact]
    public void Background_clip_content_box_clips_the_shading_to_the_content_box()
    {
        // Default clip = border-box (130×90); content-box clip = the 100×60 content box.
        var t = Latin1(HtmlPdf.Convert(Html("border:10px solid black;padding:5px;background-clip:content-box;")));
        var (w, h) = ShadingClip(t);
        Assert.Equal(100 * PtPerPx, w, 3);
        Assert.Equal(60 * PtPerPx, h, 3);
    }

    [Fact]
    public void Background_clip_defaults_to_the_border_box()
    {
        var t = Latin1(HtmlPdf.Convert(Html("border:10px solid black;padding:5px;")));
        var (w, h) = ShadingClip(t);
        Assert.Equal(130 * PtPerPx, w, 3); // border box = content 100 + padding 10 + border 20
        Assert.Equal(90 * PtPerPx, h, 3);
    }

    // ---- radial (task 2) ----

    [Fact]
    public void Radial_origin_border_box_uses_a_larger_radius_than_content_box()
    {
        // Farthest-corner radius scales with the origin box: border-box (130×130) > content-box (100×100).
        var border = RadialOuterRadius(Latin1(HtmlPdf.Convert(
            RadialHtml("border:10px solid black;padding:5px;background-origin:border-box;"))));
        var content = RadialOuterRadius(Latin1(HtmlPdf.Convert(
            RadialHtml("border:10px solid black;padding:5px;background-origin:content-box;"))));
        // content box 100×100 → r = √(50²+50²)px; border box 130×130 → r = √(65²+65²)px.
        Assert.Equal(System.Math.Sqrt(50 * 50 + 50 * 50) * PtPerPx, content, 2);
        Assert.Equal(System.Math.Sqrt(65 * 65 + 65 * 65) * PtPerPx, border, 2);
        Assert.True(border > content);
    }

    [Fact]
    public void Radial_origin_defaults_to_padding_box()
    {
        // Default origin = padding-box: content 100 + padding 10 = 110×110 → r = √(55²+55²)px.
        var r = RadialOuterRadius(Latin1(HtmlPdf.Convert(RadialHtml("border:10px solid black;padding:5px;"))));
        Assert.Equal(System.Math.Sqrt(55 * 55 + 55 * 55) * PtPerPx, r, 2);
    }

    [Fact]
    public void Radial_background_clip_content_box_clips_to_the_content_box()
    {
        var t = Latin1(HtmlPdf.Convert(RadialHtml(
            "border:10px solid black;padding:5px;background-clip:content-box;")));
        var (w, h) = ShadingClip(t);
        Assert.Equal(100 * PtPerPx, w, 3);
        Assert.Equal(100 * PtPerPx, h, 3);
    }

    [Fact]
    public void Radial_no_border_renders_a_native_radial_shading()
    {
        var t = Latin1(HtmlPdf.Convert(RadialHtml("")));
        Assert.Contains("/ShadingType 3", t);
    }

    // ---- conic (task 3) ----

    // The conic raster image placement size (w, h in pt): `q w 0 0 h x y cm /Im<n> Do`.
    private static (double W, double H) ConicPlacement(string pdf)
    {
        var m = Regex.Match(pdf, @"q ([\d.]+) 0 0 ([\d.]+) [\d.]+ [\d.]+ cm /Im\d+ Do");
        Assert.True(m.Success, "no conic image placement found");
        return (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // The clip rect (w, h in pt) wrapping the conic image: `cx cy cw ch re W n q <w> 0 0 ...`.
    private static (double W, double H) ConicClip(string pdf)
    {
        var m = Regex.Match(pdf, @"([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+) re W n\s+q [\d.]+ 0 0");
        Assert.True(m.Success, "no conic clip rect found");
        return (double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    private static string ConicHtml(string extraStyle) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;{extraStyle}background-image:conic-gradient(red, blue)\"></div>" +
        "</body></html>";

    [Fact]
    public void Conic_origin_content_box_rasters_over_the_content_box()
    {
        var (w, h) = ConicPlacement(Latin1(HtmlPdf.Convert(ConicHtml(
            "border:10px solid black;padding:5px;background-origin:content-box;"))));
        Assert.Equal(100 * PtPerPx, w, 2); // content box 100×60
        Assert.Equal(60 * PtPerPx, h, 2);
    }

    [Fact]
    public void Conic_origin_defaults_to_padding_box()
    {
        var (w, h) = ConicPlacement(Latin1(HtmlPdf.Convert(ConicHtml("border:10px solid black;padding:5px;"))));
        Assert.Equal(110 * PtPerPx, w, 2); // padding box = content 100 + padding 10
        Assert.Equal(70 * PtPerPx, h, 2);
    }

    [Fact]
    public void Conic_clip_content_box_clips_the_sweep_to_the_content_box()
    {
        var (w, h) = ConicClip(Latin1(HtmlPdf.Convert(ConicHtml(
            "border:10px solid black;padding:5px;background-clip:content-box;"))));
        Assert.Equal(100 * PtPerPx, w, 2);
        Assert.Equal(60 * PtPerPx, h, 2);
    }

    [Fact]
    public void Conic_no_border_rasters_with_an_smask()
    {
        var t = Latin1(HtmlPdf.Convert(ConicHtml("")));
        Assert.Contains(" Do", t);
        Assert.Contains("/SMask", t);
    }
}
