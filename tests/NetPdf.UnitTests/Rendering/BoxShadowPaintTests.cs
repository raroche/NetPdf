// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
    public void Sharp_inset_shadow_paints_a_native_ring_over_the_background()
    {
        var result = HtmlPdf.ConvertDetailed(Html("inset 3px 3px #cc3366"));
        var text = Latin1(result.Pdf);

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
        Assert.Contains("0.8 0.2 0.4 rg", text);   // the inset shadow band fill color
        Assert.Contains("f*", text);                // an even-odd ring (padding box minus the lit hole)
        // The inset shadow paints OVER (after) the background color, not under it.
        var shadowIdx = text.IndexOf("0.8 0.2 0.4 rg", StringComparison.Ordinal);
        var bgIdx = text.IndexOf("0.2 0.4 0.8 rg", StringComparison.Ordinal);
        Assert.True(bgIdx >= 0 && shadowIdx > bgIdx,
            "an inset box-shadow must paint OVER (after) the background color");
    }

    [Fact]
    public void Blurred_inset_shadow_rasterizes_and_emits_the_raster_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("inset 4px 4px 8px #cc3366"));
        var text = Latin1(result.Pdf);

        Assert.Contains("/Subtype /Image", text);  // the rasterized inset band placed as an image XObject
        Assert.Contains("/SMask", text);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowBlurRaster001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
    }

    [Fact]
    public void Mixed_inset_and_outset_shadows_both_paint()
    {
        // Outset paints UNDER the background; inset paints OVER it — both in the same value.
        var result = HtmlPdf.ConvertDetailed(Html("6px 6px #33cc66, inset 3px 3px #cc3366"));
        var text = Latin1(result.Pdf);
        Assert.Contains("0.2 0.8 0.4 rg", text);   // the outset shadow (#33cc66)
        Assert.Contains("0.8 0.2 0.4 rg", text);   // the inset shadow band (#cc3366)
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
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

    [Fact]
    public void Blurred_shadow_with_mixed_corner_radii_rasterizes_without_unsupported()
    {
        // A box with mixed per-corner border-radius + a blurred shadow rasterizes the shadow shape with
        // each corner's own radius (no representative-radius collapse) and places it, no unsupported flag.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:60px;border-radius:0 30px 0 30px;background-color:#3366cc;box-shadow:0 0 8px #cc3366\"></div>" +
            "</body></html>");
        var text = Latin1(result.Pdf);

        Assert.Contains("/Subtype /Image", text);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowBlurRaster001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
    }

    [Fact]
    public void Oversized_blur_falls_back_to_sharp_with_a_diagnostic()
    {
        // An 800px blur on the 100×60 box drives the raster bitmap past the 4096px cap → sharp
        // fallback, SURFACED via CSS-BOXSHADOW-UNSUPPORTED-001 (PR #210 review [P2]).
        var result = HtmlPdf.ConvertDetailed(Html("0 0 800px #cc3366"));

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBoxShadowUnsupported001);
        Assert.Contains("0.8 0.2 0.4 rg", Latin1(result.Pdf)); // a sharp shadow still paints
    }
}
