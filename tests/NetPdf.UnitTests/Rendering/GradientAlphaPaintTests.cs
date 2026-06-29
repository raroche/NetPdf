// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradient refinements — per-stop ALPHA. A native axial / radial shading is
/// DeviceRGB (no alpha), so a gradient with a translucent stop falls back to a Skia raster (an image
/// XObject + alpha <c>/SMask</c>); a fully-opaque gradient stays a native shading.</summary>
public sealed class GradientAlphaPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Html(string bg) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:60px;background-image:{bg}\"></div>" +
        "</body></html>";

    [Fact]
    public void Translucent_linear_gradient_falls_back_to_a_raster_with_an_smask()
    {
        var t = Latin1(HtmlPdf.Convert(Html("linear-gradient(rgba(255,0,0,0.5), blue)")));
        Assert.Contains(" Do", t);                 // placed as an image XObject
        Assert.Contains("/SMask", t);              // ... with an alpha soft-mask
        Assert.DoesNotContain("/ShadingType 2", t); // NOT a native axial shading
    }

    [Fact]
    public void Opaque_linear_gradient_stays_a_native_shading()
    {
        var t = Latin1(HtmlPdf.Convert(Html("linear-gradient(red, blue)")));
        Assert.Contains("/ShadingType 2", t);       // native axial shading (byte-identical path)
        Assert.DoesNotContain(" Do", t);            // no raster image
    }

    [Fact]
    public void A_transparent_keyword_stop_is_translucent_and_rasters()
    {
        // `transparent` = rgba(0,0,0,0) — a translucent stop, so the gradient rasters.
        var t = Latin1(HtmlPdf.Convert(Html("linear-gradient(red, transparent)")));
        Assert.Contains(" Do", t);
        Assert.Contains("/SMask", t);
    }

    [Fact]
    public void Translucent_radial_gradient_falls_back_to_a_raster_with_an_smask()
    {
        var t = Latin1(HtmlPdf.Convert(Html("radial-gradient(rgba(0,255,0,0.4), blue)")));
        Assert.Contains(" Do", t);
        Assert.Contains("/SMask", t);
        Assert.DoesNotContain("/ShadingType 3", t); // NOT a native radial shading
    }

    [Fact]
    public void Opaque_radial_gradient_stays_a_native_shading()
    {
        var t = Latin1(HtmlPdf.Convert(Html("radial-gradient(red, blue)")));
        Assert.Contains("/ShadingType 3", t);       // native radial shading (byte-identical path)
        Assert.DoesNotContain(" Do", t);
    }

    // PR #237 review [P1] — when a translucent gradient over-caps the raster, it must NOT silently fall
    // through to an opaque native shading (which would DROP the transparency). It is skipped (the
    // background-color shows) under a distinct Warning, like conic over-cap.
    private static string OversizedHtml(string bg) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:3000px;height:50px;background-image:{bg}\"></div>" +
        "</body></html>";

    [Fact]
    public void Oversized_translucent_linear_skips_with_a_warning_not_an_opaque_shading()
    {
        // 3000px-wide box → alpha bitmap (× 2) blows past the 4096 px cap.
        var result = HtmlPdf.ConvertDetailed(OversizedHtml("linear-gradient(rgba(255,0,0,0.5), blue)"));
        var warn = result.Warnings.Single(d => d.Code == DiagnosticCodes.CssGradientAlphaUnsupported001);
        Assert.Equal(DiagnosticSeverity.Warning, warn.Severity);
        var t = Latin1(result.Pdf);
        Assert.DoesNotContain("/ShadingType 2", t); // did NOT drop alpha into an opaque axial shading
        Assert.DoesNotContain(" Do", t);            // and did not place a raster image either
    }

    [Fact]
    public void Oversized_translucent_radial_skips_with_a_warning_not_an_opaque_shading()
    {
        var result = HtmlPdf.ConvertDetailed(OversizedHtml("radial-gradient(rgba(0,255,0,0.4), blue)"));
        var warn = result.Warnings.Single(d => d.Code == DiagnosticCodes.CssGradientAlphaUnsupported001);
        Assert.Equal(DiagnosticSeverity.Warning, warn.Severity);
        var t = Latin1(result.Pdf);
        Assert.DoesNotContain("/ShadingType 3", t); // did NOT drop alpha into an opaque radial shading
        Assert.DoesNotContain(" Do", t);
    }

    [Fact]
    public void Oversized_OPAQUE_gradient_still_paints_natively_no_alpha_warning()
    {
        // The over-cap skip is alpha-only: an opaque gradient at any size stays a native shading and
        // never raises the alpha-unsupported Warning (it doesn't go through the raster at all).
        var result = HtmlPdf.ConvertDetailed(OversizedHtml("linear-gradient(red, blue)"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssGradientAlphaUnsupported001);
        Assert.Contains("/ShadingType 2", Latin1(result.Pdf));
    }
}
