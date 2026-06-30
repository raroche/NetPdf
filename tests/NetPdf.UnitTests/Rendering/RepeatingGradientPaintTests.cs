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
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients (PR 1 refinements) — repeating-linear / repeating-radial render as
/// NATIVE PDF shadings (not raster): the resolved stop period is tiled across the gradient line, so
/// a <c>repeating-*</c> gradient emits strictly more FunctionType 2 sub-functions than the plain
/// form. Page content is uncompressed, so the shading objects are string-inspectable.</summary>
public sealed class RepeatingGradientPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static int Count(string haystack, string needle) => haystack.Split(needle).Length - 1;

    private static IEnumerable<double> AllBoundsFlat(string pdf) =>
        Regex.Matches(pdf, @"/Bounds \[([^\]]*)\]")
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture));

    [Fact]
    public void Repeating_period_is_last_minus_first_specified_stop()
    {
        // repeating-linear-gradient(to right, red 10px, blue 50px) on a 100px box → period =
        // (50-10)/100 = 0.4, so the cycle seams fall at 0.1, 0.5, 0.9 (NOT 0.5/0.6 of the buggy
        // last-offset period). The function /Bounds therefore include 0.9 and NOT 0.6. (PR 226 [P1])
        var bounds = AllBoundsFlat(
            Latin1(HtmlPdf.Convert(Html("repeating-linear-gradient(to right, red 10px, blue 50px)")))).ToList();
        Assert.Contains(bounds, v => Math.Abs(v - 0.9) < 1e-4);
        Assert.DoesNotContain(bounds, v => Math.Abs(v - 0.6) < 1e-4);
    }

    private static string Html(string gradient) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:40px;background-image:{gradient}\"></div>" +
        "</body></html>";

    [Fact]
    public void Repeating_linear_tiles_natively_into_more_subfunctions()
    {
        // `to right` over a 100px box, period 25px → 4 tiles; the plain form is a single ramp.
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-linear-gradient(to right, red, blue 25px)")));
        var plain = Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red, blue 25px)")));

        Assert.Contains("/ShadingType 2", rep);                       // native axial shading, no raster
        Assert.True(Count(rep, "/FunctionType 2") > Count(plain, "/FunctionType 2"),
            "the repeating gradient must tile into more sub-functions than the plain form");
        // It is NOT a raster fallback (no conic-style image diagnostic / XObject for this layer).
        var result = HtmlPdf.ConvertDetailed(Html("repeating-linear-gradient(to right, red, blue 25px)"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void Repeating_radial_renders_a_native_radial_shading()
    {
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-radial-gradient(circle farthest-corner, red, blue 15px)")));
        var plain = Latin1(HtmlPdf.Convert(Html("radial-gradient(circle farthest-corner, red, blue 15px)")));
        Assert.Contains("/ShadingType 3", rep);                       // native radial shading
        Assert.True(Count(rep, "/FunctionType 2") > Count(plain, "/FunctionType 2"));
    }

    private static double RadialShadingOuterRadius(string pdf)
    {
        // ShadingType 3 /Coords = [x0 y0 r0 x1 y1 r1]; the 6th value is the outer radius (pt).
        var m = Regex.Match(pdf, @"/Coords \[([^\]]*)\]");
        Assert.True(m.Success, "a radial shading /Coords must be present");
        var nums = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        return nums[5];
    }

    [Fact]
    public void Repeating_radial_closest_side_repeats_out_to_the_farthest_corner()
    {
        // On the 100×40 box, a centered circle closest-side ends at 20px (the half-height → 15pt). A
        // NON-repeating gradient clamps there (the last color extends past it); the REPEATING one must keep
        // repeating to the box's farthest corner (√(50² + 20²) ≈ 53.85px → ≈ 40.4pt), so its shading outer
        // radius is the farthest corner, not the ending shape — and it tiles into more sub-functions.
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-radial-gradient(circle closest-side, red, blue 5px)")));
        var plain = Latin1(HtmlPdf.Convert(Html("radial-gradient(circle closest-side, red, blue 5px)")));

        Assert.Equal(15.0, RadialShadingOuterRadius(plain), 1);   // non-repeating clamps at the ending shape
        var repR = RadialShadingOuterRadius(rep);
        Assert.True(repR > 40.0 && repR < 41.0, $"repeating must reach the farthest corner (~40.4pt); got {repR}");
        Assert.True(Count(rep, "/FunctionType 2") > Count(plain, "/FunctionType 2"),
            "the repeat now spans the larger radius → more tiled sub-functions");
    }

    [Fact]
    public void Off_center_repeating_radial_closest_side_repeats_to_the_farthest_corner()
    {
        // PR #247 [P3-coverage] — center at 25% 25% (off-center) on the 100×40 box: closest-side =
        // min(25, 10) = 10px (15→7.5pt non-repeating); the farthest corner is the bottom-right at (75, 30) →
        // √(75² + 30²) ≈ 80.8px → ≈ 60.6pt. The repeating gradient must reach that corner.
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-radial-gradient(circle closest-side at 25% 25%, red, blue 4px)")));
        var plain = Latin1(HtmlPdf.Convert(Html("radial-gradient(circle closest-side at 25% 25%, red, blue 4px)")));
        Assert.Equal(7.5, RadialShadingOuterRadius(plain), 1);
        Assert.True(RadialShadingOuterRadius(rep) > 60.0,
            $"off-center repeating closest-side must reach the farthest corner (~60.6pt); got {RadialShadingOuterRadius(rep)}");
    }

    [Fact]
    public void Repeating_radial_farthest_corner_is_unchanged_no_over_extension()
    {
        // farthest-corner already reaches the corner ⇒ coverExtent = 1 ⇒ the outer radius equals the
        // non-repeating one (no spurious extension): the existing behavior is preserved.
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-radial-gradient(circle farthest-corner, red, blue 5px)")));
        var plain = Latin1(HtmlPdf.Convert(Html("radial-gradient(circle farthest-corner, red, blue 5px)")));
        Assert.Equal(RadialShadingOuterRadius(plain), RadialShadingOuterRadius(rep), 1);
    }

    [Fact]
    public void Repeating_with_full_turn_period_matches_the_plain_gradient()
    {
        // Unpositioned stops → last at 100% → period 1 → no repetition (≡ the plain gradient).
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-linear-gradient(to right, red, blue)")));
        var plain = Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red, blue)")));
        Assert.Equal(Count(plain, "/FunctionType 2"), Count(rep, "/FunctionType 2"));
    }
}
