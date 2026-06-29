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

    // The clip rect (w, h in pt) the shading is painted into: `x y w h re W n /Sh<n> sh`.
    private static (double W, double H) ShadingClip(string pdf)
    {
        var m = Regex.Match(pdf, @"([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+) re W n /Sh\d+ sh");
        Assert.True(m.Success, "no shading clip rect found");
        return (double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
    }

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
}
