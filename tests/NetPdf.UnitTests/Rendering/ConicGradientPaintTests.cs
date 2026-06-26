// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Linq;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients (PR 1 refinements) — end-to-end <c>conic-gradient</c> painting: PDF
/// has no native conic shading, so the sweep rasterizes via Skia and places an image XObject (with
/// an alpha <c>/SMask</c>) clipped to the box, surfacing <c>CSS-CONIC-GRADIENT-RASTER-001</c>. Page
/// content is uncompressed, so the operators / dictionaries are string-inspectable.</summary>
public sealed class ConicGradientPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Html(string backgroundImage) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:120px;height:80px;background-image:{backgroundImage}\"></div>" +
        "</body></html>";

    [Fact]
    public void Conic_gradient_rasterizes_to_an_image_and_emits_the_raster_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("conic-gradient(red, lime, blue)"));
        var text = Latin1(result.Pdf);

        Assert.Contains("/Subtype /Image", text);  // the rasterized sweep placed as an image XObject
        Assert.Contains("/SMask", text);           // alpha plane carried alongside the RGB plane
        Assert.Contains("Do", text);               // the image is drawn
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void From_angle_and_at_position_conic_renders()
    {
        var result = HtmlPdf.ConvertDetailed(Html("conic-gradient(from 90deg at 25% 75%, red, blue)"));
        Assert.Contains("/Subtype /Image", Latin1(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void Repeating_conic_gradient_renders_via_raster()
    {
        var result = HtmlPdf.ConvertDetailed(Html("repeating-conic-gradient(red 0deg, blue 30deg)"));
        Assert.Contains("/Subtype /Image", Latin1(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void Conic_with_translucent_stops_carries_an_smask()
    {
        // Per-stop alpha is preserved through the raster's /SMask (a native PDF shading can't).
        var result = HtmlPdf.ConvertDetailed(
            Html("conic-gradient(rgba(255,0,0,0.4), rgba(0,0,255,0.9))"));
        var text = Latin1(result.Pdf);
        Assert.Contains("/Subtype /Image", text);
        Assert.Contains("/SMask", text);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void A_linear_gradient_does_not_emit_the_conic_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("linear-gradient(red, blue)"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void Out_of_range_conic_stops_render()
    {
        // white -180deg, black 540deg → stops kept raw at -0.5 / 1.5 turn; the sweep clips to [0,1]
        // with interpolated boundary colors (PR 226 review [P1]). It still rasterizes successfully.
        var result = HtmlPdf.ConvertDetailed(Html("conic-gradient(white -180deg, black 540deg)"));
        Assert.Contains("/Subtype /Image", Latin1(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }

    [Fact]
    public void Successful_conic_raster_is_info_only_severity()
    {
        // The raster-fallback code is Info — never Warning (PR 226 review [P2] diagnostic parity).
        var result = HtmlPdf.ConvertDetailed(Html("conic-gradient(red, lime, blue)"));
        var diag = result.Warnings.Single(d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
        Assert.Equal(DiagnosticSeverity.Info, diag.Severity);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientUnsupported001);
    }

    [Fact]
    public void Oversized_conic_skips_with_a_distinct_warning_not_the_info_code()
    {
        // A 3000px-wide box drives the sweep bitmap (× 2) past the 4096 px cap → the gradient is
        // SKIPPED under the distinct Warning code, and the Info raster code is NOT emitted.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:3000px;height:50px;background-image:conic-gradient(red, blue)\"></div>" +
            "</body></html>");
        var warn = result.Warnings.Single(d => d.Code == DiagnosticCodes.CssConicGradientUnsupported001);
        Assert.Equal(DiagnosticSeverity.Warning, warn.Severity);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssConicGradientRaster001);
    }
}
