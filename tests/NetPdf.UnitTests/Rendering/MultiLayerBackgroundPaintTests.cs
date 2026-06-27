// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 multi-layer backgrounds — a comma-separated <c>background-image</c> list paints all
/// layers (back-to-front), mixing url() images (<c>Do</c>) + linear (<c>/ShadingType 2</c>) / radial
/// (<c>/ShadingType 3</c>) / conic gradients, each with its own position/size/repeat/origin/clip. A single
/// layer keeps the existing path (byte-identical). Page content is uncompressed.</summary>
public sealed class MultiLayerBackgroundPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Png() =>
        "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(16, 16));

    private static string Html(string bg, string extra = "") =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:120px;height:80px;background-image:{bg};{extra}\"></div>" +
        "</body></html>";

    private static int Count(string h, string n)
    {
        var c = 0;
        for (var i = h.IndexOf(n, StringComparison.Ordinal); i >= 0; i = h.IndexOf(n, i + n.Length, StringComparison.Ordinal)) c++;
        return c;
    }

    [Fact]
    public void Two_linear_gradient_layers_both_paint()
    {
        var t = Latin1(HtmlPdf.Convert(Html("linear-gradient(red, blue), linear-gradient(lime, yellow)")));
        Assert.Equal(2, Count(t, "/ShadingType 2"));   // two distinct axial shadings
    }

    [Fact]
    public void Linear_and_radial_layers_each_paint_their_native_shading()
    {
        var t = Latin1(HtmlPdf.Convert(Html("linear-gradient(red, blue), radial-gradient(lime, yellow)")));
        Assert.Equal(1, Count(t, "/ShadingType 2"));   // linear
        Assert.Equal(1, Count(t, "/ShadingType 3"));   // radial
    }

    [Fact]
    public void Image_and_gradient_layers_both_paint()
    {
        var t = Latin1(HtmlPdf.Convert(Html($"url({Png()}), linear-gradient(red, blue)")));
        Assert.Contains("Do", t);                      // the image layer
        Assert.Equal(1, Count(t, "/ShadingType 2"));   // the gradient layer
    }

    [Fact]
    public void Three_layers_all_paint()
    {
        var t = Latin1(HtmlPdf.Convert(Html(
            $"url({Png()}), linear-gradient(red, blue), radial-gradient(lime, yellow)")));
        Assert.Contains("Do", t);
        Assert.Equal(1, Count(t, "/ShadingType 2"));
        Assert.Equal(1, Count(t, "/ShadingType 3"));
    }

    [Fact]
    public void A_none_layer_is_a_no_op_slot_and_does_not_break_the_list()
    {
        var result = HtmlPdf.ConvertDetailed(Html("linear-gradient(red, blue), none"));
        Assert.Equal(1, Count(Latin1(result.Pdf), "/ShadingType 2"));   // the gradient still paints
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
    }

    [Fact]
    public void Background_color_still_paints_under_the_layers()
    {
        var t = Latin1(HtmlPdf.Convert(Html(
            "linear-gradient(red, blue), radial-gradient(lime, yellow)", "background-color:#336699")));
        Assert.Contains("0.2 0.4 0.6 rg", t);          // the color fills under both layers
        Assert.Equal(1, Count(t, "/ShadingType 2"));
        Assert.Equal(1, Count(t, "/ShadingType 3"));
    }

    [Fact]
    public void Per_layer_position_changes_the_output()
    {
        // Two image layers; giving the second layer its own position must change the bytes vs the default.
        var def = Latin1(HtmlPdf.Convert(Html($"url({Png()}), url({Png()})", "background-repeat:no-repeat")));
        var pos = Latin1(HtmlPdf.Convert(Html($"url({Png()}), url({Png()})",
            "background-repeat:no-repeat;background-position:0 0, 40px 30px")));
        Assert.Contains("Do", def);
        Assert.NotEqual(def, pos);                      // the per-layer position shifted the second image
    }

    [Fact]
    public void Single_gradient_layer_is_unchanged_by_the_multi_layer_path()
    {
        // A single layer never enters the multi-layer path — still one shading, no diagnostic.
        var result = HtmlPdf.ConvertDetailed(Html("linear-gradient(red, blue)"));
        Assert.Equal(1, Count(Latin1(result.Pdf), "/ShadingType 2"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
    }
}
