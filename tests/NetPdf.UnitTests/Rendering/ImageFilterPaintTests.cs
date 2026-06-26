// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 filters (PR 2) — end-to-end: a CSS <c>filter</c> on an <c>&lt;img&gt;</c> applies
/// via the Skia raster fallback (the decoded image runs through the filter chain and re-embeds as a
/// raster XObject), surfacing <c>CSS-FILTER-RASTER-FALLBACK-001</c>. Page content is uncompressed.</summary>
public sealed class ImageFilterPaintTests
{
    private static string DataUriPng()
    {
        var png = SyntheticRasterImage.BuildOpaquePng(8, 8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static string Html(string filter)
    {
        var styleFilter = filter.Length == 0 ? "" : $";filter:{filter}";
        return "<!DOCTYPE html><html><body>" +
            $"<img src=\"{DataUriPng()}\" style=\"width:32px;height:32px{styleFilter}\">" +
            "</body></html>";
    }

    [Fact]
    public void Grayscale_filter_on_an_img_rasterizes_and_emits_the_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("grayscale(100%)"));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssFilterRasterFallback001);
    }

    [Fact]
    public void Multiple_color_filters_chain_on_an_img()
    {
        var result = HtmlPdf.ConvertDetailed(Html("grayscale(100%) brightness(1.2) invert(100%)"));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssFilterRasterFallback001);
    }

    [Fact]
    public void Blur_filter_on_an_img_rasterizes_and_emits_the_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html("blur(2px)"));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssFilterRasterFallback001);
    }

    [Fact]
    public void Blur_chained_with_a_color_filter_on_an_img()
    {
        var result = HtmlPdf.ConvertDetailed(Html("grayscale(100%) blur(3px)"));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(result.Pdf));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssFilterRasterFallback001);
    }

    [Fact]
    public void An_unfiltered_img_emits_no_filter_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html(""));
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(result.Pdf));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssFilterRasterFallback001);
    }
}
