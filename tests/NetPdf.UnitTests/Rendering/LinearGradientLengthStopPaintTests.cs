// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients (PR 1 refinements) — end-to-end length-positioned color stops:
/// a <c>blue 50px</c> stop on a 100px-wide <c>to right</c> gradient resolves to the 0.5 fraction
/// (the FunctionType 3 <c>/Bounds</c>), byte-equivalent to the same gradient written with <c>50%</c>.
/// Page content is uncompressed, so the shading function is string-inspectable.</summary>
public sealed class LinearGradientLengthStopPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static double[][] AllBounds(string pdf) =>
        Regex.Matches(pdf, @"/Bounds \[([^\]]*)\]")
            .Select(m => m.Groups[1].Value
                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();

    private static string Html(string gradient) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:40px;background-image:{gradient}\"></div>" +
        "</body></html>";

    [Fact]
    public void Px_stop_resolves_against_the_gradient_line_length()
    {
        // `to right` over a 100px-wide box → gradient line = 100px; blue 50px → fraction 0.5.
        var text = Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red, blue 50px, lime)")));
        Assert.Contains("/ShadingType 2", text);   // native axial shading (not a bg-color fallback)
        var bounds = AllBounds(text);
        Assert.Contains(bounds, b => b.Length == 1 && System.Math.Abs(b[0] - 0.5) < 1e-6);
    }

    [Fact]
    public void Px_and_percentage_stops_produce_identical_shading_bounds()
    {
        var px = AllBounds(Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red, blue 50px, lime)"))));
        var pct = AllBounds(Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red, blue 50%, lime)"))));
        Assert.NotEmpty(pct);
        Assert.Equal(pct.Select(b => string.Join(',', b)), px.Select(b => string.Join(',', b)));
    }

    [Fact]
    public void Out_of_range_stops_tint_the_visible_endpoints()
    {
        // red -50px, blue 150px on a 100px `to right` box → red@-0.5, blue@1.5. The visible line [0,1]
        // shows the INTERPOLATED slice: color at 0 = mix(red,blue, 0.5/2.0 = 0.25) = (0.75, 0, 0.25),
        // at 1 = mix(red,blue, 1.5/2.0 = 0.75) = (0.25, 0, 0.75). Out-of-range stops are NOT clamped to
        // the box ends (PR 226 review [P1]).
        var text = Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red -50px, blue 150px)")));
        Assert.Contains("/ShadingType 2", text);
        Assert.Contains("0.75 0 0.25", text);          // the start-edge mix (C0)
        Assert.Contains("0.25 0 0.75", text);          // the end-edge mix (C1)
        Assert.DoesNotContain("/C0 [1 0 0]", text);     // pure red never reaches the visible line
    }

    [Fact]
    public void Absolute_unit_stop_renders_a_native_shading()
    {
        // 0.75in = 72px on a 96px box → fraction 0.75.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:96px;height:40px;background-image:linear-gradient(to right, red, blue 0.75in, lime)\"></div>" +
            "</body></html>"));
        Assert.Contains("/ShadingType 2", text);
        Assert.Contains(AllBounds(text), b => b.Length == 1 && System.Math.Abs(b[0] - 0.75) < 1e-6);
    }
}
