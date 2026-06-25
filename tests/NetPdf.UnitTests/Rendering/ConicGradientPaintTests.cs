// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
}
