// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>Every shading/clip rectangle (<c>x y w h re W n</c>) in DOCUMENT order, as (w, h) in
    /// PDF points. PdfPage writes numbers with the <c>0.#####</c> format, so a whole-pt edge prints as
    /// a clean integer. px→pt is ×0.75.</summary>
    private static List<(double W, double H)> ClipRects(string content)
    {
        var rects = new List<(double, double)>();
        const string marker = " re W n";
        for (var i = content.IndexOf(marker, StringComparison.Ordinal); i >= 0;
             i = content.IndexOf(marker, i + marker.Length, StringComparison.Ordinal))
        {
            var start = Math.Max(0, i - 80);
            var window = content.Substring(start, i - start);
            var toks = window.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 4) continue;
            if (double.TryParse(toks[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                && double.TryParse(toks[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
            {
                rects.Add((w, h));
            }
        }
        return rects;
    }

    private static string BadPng() => "data:image/png;base64,AAAAAAAA"; // valid base64, not a decodable PNG

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

    [Fact]
    public void Gradient_layers_paint_back_to_front_with_their_own_background_clip()
    {
        // content 100×60, padding 10 (no border) → padding/border box 120×80, content box 100×60.
        // Source order: layer0 (top) = red/blue with content-box clip; layer1 (bottom) = lime/yellow with
        // border-box clip. Back-to-front means the BOTTOM layer (border-box, 90×60pt) paints FIRST and the
        // TOP layer (content-box, 75×45pt) paints LAST.
        var t = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body><div style=\"width:100px;height:60px;padding:10px;"
            + "background-image:linear-gradient(red,blue),linear-gradient(lime,yellow);"
            + "background-clip:content-box,border-box\"></div></body></html>"));
        var rects = ClipRects(t);
        Assert.Equal(2, rects.Count);
        Assert.Equal((90.0, 60.0), rects[0]);   // bottom layer (border-box) painted first
        Assert.Equal((75.0, 45.0), rects[1]);   // top layer (content-box) painted last
    }

    [Fact]
    public void Gradient_layer_background_origin_changes_the_axis()
    {
        // border 20 (no padding) → border box 160×120, padding box 120×80. Two SAME-color gradient layers:
        // distinct origins ⇒ distinct axes ⇒ two shading objects (each with its own /Coords); identical
        // origins ⇒ one cached shading (one /Coords). Proves origin actually moves a gradient's axis.
        static string Doc(string origin) =>
            "<!DOCTYPE html><html><body><div style=\"width:120px;height:80px;border:20px solid black;"
            + "background-image:linear-gradient(red,blue),linear-gradient(red,blue);"
            + $"background-origin:{origin}\"></div></body></html>";

        var distinct = Latin1(HtmlPdf.Convert(Doc("border-box,padding-box")));
        var same = Latin1(HtmlPdf.Convert(Doc("border-box,border-box")));
        Assert.Equal(2, Count(distinct, "/Coords"));   // two different axes
        Assert.Equal(1, Count(same, "/Coords"));        // identical axes share one shading
    }

    [Fact]
    public void Gradient_layer_size_position_repeat_are_deferred_with_a_diagnostic()
    {
        // background-size/-position/-repeat are not honoured for a gradient layer (the shading fills the
        // origin box) — each surfaces CSS-BACKGROUND-IMAGE-UNSUPPORTED-001 once; the gradients still paint.
        foreach (var variant in new[]
                 {
                     "background-size:cover,cover",
                     "background-position:10px 10px,20px 20px",
                     "background-repeat:no-repeat,no-repeat",
                 })
        {
            var r = HtmlPdf.ConvertDetailed(Html("linear-gradient(red,blue), radial-gradient(lime,yellow)", variant));
            Assert.Contains(r.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
            Assert.Equal(1, Count(Latin1(r.Pdf), "/ShadingType 2"));
            Assert.Equal(1, Count(Latin1(r.Pdf), "/ShadingType 3"));
        }
    }

    [Fact]
    public void Default_variants_on_gradient_layers_emit_no_deferral_diagnostic()
    {
        // Initial size/position/repeat (auto / 0% 0% / repeat) are no-ops for a gradient layer.
        var r = HtmlPdf.ConvertDetailed(Html(
            "linear-gradient(red,blue), radial-gradient(lime,yellow)",
            "background-size:auto,auto;background-position:0% 0%,0% 0%;background-repeat:repeat,repeat"));
        Assert.DoesNotContain(r.Warnings, d => d.Code == DiagnosticCodes.CssBackgroundImageUnsupported001);
    }

    [Fact]
    public void A_url_layer_that_fails_to_decode_does_not_prevent_other_layers()
    {
        // The url layer parses (so the list is supported) but its bytes don't decode → it's skipped, while
        // the gradient layer still paints.
        var t = Latin1(HtmlPdf.Convert(Html($"url({BadPng()}), linear-gradient(red,blue)")));
        Assert.Equal(1, Count(t, "/ShadingType 2"));   // the gradient still paints
        Assert.DoesNotContain(" Do Q", t);              // the undecodable image painted nothing
    }
}
