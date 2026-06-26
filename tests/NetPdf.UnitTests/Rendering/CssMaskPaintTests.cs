// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.Pdf.Images;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 mask (PR 4) — a <c>mask</c>/<c>mask-image</c> on an <c>&lt;img&gt;</c> composites the
/// image's alpha with the mask's alpha via Skia + re-embeds it as an XObject with a <c>/SMask</c>; a mask
/// on a general element is diagnosed (needs the subtree renderer). Page content is uncompressed.</summary>
public sealed class CssMaskPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
    private static string DataPng(int w = 16, int h = 16) =>
        "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(w, h));

    [Fact]
    public void ImageMaskApplier_produces_an_xobject_with_a_soft_mask()
    {
        var result = ImageMaskApplier.TryApply(
            SyntheticRasterImage.BuildOpaquePng(8, 8), SyntheticRasterImage.BuildOpaquePng(8, 8));
        Assert.NotNull(result);
        Assert.NotNull(result!.SMask);   // RGBA composite → an alpha soft-mask is emitted
    }

    [Fact]
    public void ImageMaskApplier_returns_null_on_undecodable_input()
    {
        Assert.Null(ImageMaskApplier.TryApply([1, 2, 3], SyntheticRasterImage.BuildOpaquePng(8, 8)));
    }

    [Fact]
    public void Mask_on_img_is_applied_via_the_raster_fallback()
    {
        var html = "<!DOCTYPE html><html><body>" +
            $"<img src=\"{DataPng()}\" style=\"width:32px;height:32px;mask-image:url({DataPng()})\">" +
            "</body></html>";
        var result = HtmlPdf.ConvertDetailed(html);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssMaskRasterFallback001);
        Assert.Contains("Do", Latin1(result.Pdf));   // the masked image still draws
    }

    [Fact]
    public void Mask_on_a_non_image_element_warns_and_paints_unmasked()
    {
        var html = "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:50px;height:50px;background:red;mask-image:url({DataPng()})\"></div>" +
            "</body></html>";
        var result = HtmlPdf.ConvertDetailed(html);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssMaskElementUnsupported001);
        Assert.Contains("1 0 0 rg", Latin1(result.Pdf)); // the red background still paints (unmasked)
    }

    [Fact]
    public void No_mask_emits_no_mask_diagnostics()
    {
        var html = "<!DOCTYPE html><html><body>" +
            $"<img src=\"{DataPng()}\" style=\"width:32px;height:32px\">" +
            "</body></html>";
        var result = HtmlPdf.ConvertDetailed(html);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssMaskRasterFallback001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssMaskElementUnsupported001);
    }
}
