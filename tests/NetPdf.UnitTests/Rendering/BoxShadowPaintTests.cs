// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Linq;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 shadows — end-to-end <c>box-shadow</c> painting: a sharp (blur = 0) outset
/// shadow paints as a native filled rect UNDER the background; a blurred shadow rasterizes via the
/// Skia bridge and places an image; inset + unsupported forms surface a diagnostic. Page content is
/// uncompressed, so the operators are string-inspectable.</summary>
public sealed class BoxShadowPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // bg #3366cc → (0.2, 0.4, 0.8); shadow #cc3366 → (0.8, 0.2, 0.4) — distinct, unambiguous fills.
    private static string Html(string boxShadow) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;background-color:#3366cc;box-shadow:{boxShadow}\"></div>" +
        "</body></html>";

    [Fact]
    public void Sharp_shadow_paints_a_native_fill_under_the_background()
    {
        var text = Latin1(HtmlPdf.Convert(Html("6px 6px #cc3366")));

        Assert.Contains("0.8 0.2 0.4 rg", text);   // the sharp shadow fill color
        Assert.Contains("re f", text);
        var shadowIdx = text.IndexOf("0.8 0.2 0.4 rg", StringComparison.Ordinal);
        var bgIdx = text.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(shadowIdx >= 0 && bgIdx >= 0 && shadowIdx < bgIdx,
            "the box-shadow must paint UNDER (before) the background color");
    }

    [Fact]
    public void Blurred_shadow_rasterizes_and_emits_the_raster_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("6px 6px 8px #cc3366"));
        var text = Latin1(result.Pdf);

        Assert.Contains("/Subtype /Image", text);  // the rasterized shadow placed as an image XObject
        Assert.Contains("/SMask", text);           // alpha plane (the blurred coverage)
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowBlurRaster001);
        // It is NOT painted as a flat native fill (no sharp shadow rect in the shadow color).
        Assert.DoesNotContain("0.8 0.2 0.4 rg", text);
    }

    [Fact]
    public void Inset_shadow_is_skipped_with_an_unsupported_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("inset 3px 3px #cc3366"));

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
        Assert.DoesNotContain("0.8 0.2 0.4 rg", Latin1(result.Pdf)); // inset not painted (first cut)
    }

    [Fact]
    public void Unsupported_unit_surfaces_the_unsupported_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("2em 2em #cc3366")); // em not resolved here
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
    }

    [Fact]
    public void None_paints_no_shadow_and_emits_no_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("none"));

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowBlurRaster001);
    }
}
