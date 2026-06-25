// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
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

    [Fact]
    public void Repeating_with_full_turn_period_matches_the_plain_gradient()
    {
        // Unpositioned stops → last at 100% → period 1 → no repetition (≡ the plain gradient).
        var rep = Latin1(HtmlPdf.Convert(Html("repeating-linear-gradient(to right, red, blue)")));
        var plain = Latin1(HtmlPdf.Convert(Html("linear-gradient(to right, red, blue)")));
        Assert.Equal(Count(plain, "/FunctionType 2"), Count(rep, "/FunctionType 2"));
    }
}
