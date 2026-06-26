// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 border-image (PR 4) — end-to-end: a decoded <c>border-image</c> slices its source into
/// the 9 border regions (each a clipped image placement, <c>re W n … cm … Do</c>) and REPLACES the normal
/// border rendering. Page content is uncompressed, so the operators are string-inspectable.</summary>
public sealed class BorderImagePaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string DataUri() =>
        "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(90, 90));

    private static string Html(string borderImage) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:120px;height:120px;border:30px solid #000;border-image:{borderImage}\"></div>" +
        "</body></html>";

    [Fact]
    public void Border_image_slices_the_source_and_replaces_the_border()
    {
        var text = Latin1(HtmlPdf.Convert(Html($"url({DataUri()}) 30 fill")));
        Assert.Contains("Do", text);                          // image slices placed
        Assert.Contains("re\nW n", text.Replace(" W n", "\nW n")); // each slice clips to its dest rect
        Assert.DoesNotContain("0 0 0 rg", text);              // the solid black border is NOT painted
    }

    [Fact]
    public void Non_stretch_repeat_is_approximated_with_a_diagnostic()
    {
        var result = HtmlPdf.ConvertDetailed(Html($"url({DataUri()}) 30 round"));
        Assert.Contains("Do", Latin1(result.Pdf));            // still paints (stretched)
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBorderImageUnsupported001);
    }

    [Fact]
    public void No_border_image_falls_back_to_the_normal_border()
    {
        // No border-image → the solid black border still fills.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:120px;height:120px;border:30px solid #000\"></div>" +
            "</body></html>"));
        Assert.Contains("0 0 0 rg", text);                    // the normal border paints
    }

    // ---- PR-229 review fixes ----

    [Fact]
    public void Later_source_none_overrides_earlier_shorthand_by_source_order()
    {
        // [P2] cascade order: `border-image: url(...)` then a LATER `border-image-source: none` → no
        // border-image (the normal solid border paints). Pre-fix the longhand always won regardless of order.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:120px;height:120px;border:30px solid #000;border-image:url({DataUri()}) 30 fill;border-image-source:none\"></div>" +
            "</body></html>"));
        Assert.Contains("0 0 0 rg", text);                    // border-image overridden → solid border paints
    }

    [Fact]
    public void Later_shorthand_overrides_earlier_source_none_by_source_order()
    {
        // The reverse: `border-image-source: none` then a LATER `border-image: url(...)` → the image paints.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:120px;height:120px;border:30px solid #000;border-image-source:none;border-image:url({DataUri()}) 30 fill\"></div>" +
            "</body></html>"));
        Assert.Contains("Do", text);                          // border-image wins
        Assert.DoesNotContain("0 0 0 rg", text);
    }

    [Fact]
    public void Border_image_paints_even_with_print_backgrounds_false()
    {
        // [P2] border-image paints the BORDER area, which renders regardless of PrintBackgrounds (like a
        // normal border) — not gated like background-image.
        var text = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<div style=\"width:120px;height:120px;border:30px solid transparent;border-image:url({DataUri()}) 30 fill\"></div>" +
            "</body></html>",
            new HtmlPdfOptions { PrintBackgrounds = false }));
        Assert.Contains("Do", text);
    }

    [Fact]
    public void Width_or_outset_is_diagnosed()
    {
        // [P3] an ignored border-image-width / -outset is diagnosed (not just non-stretch repeat).
        var result = HtmlPdf.ConvertDetailed(Html($"url({DataUri()}) 30 / 10px / 5px"));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBorderImageUnsupported001);
    }

    [Fact]
    public void Gradient_source_is_diagnosed_and_not_painted()
    {
        // [P3] a non-url() (gradient) border-image-source is unsupported → diagnosed, normal border paints.
        var result = HtmlPdf.ConvertDetailed(
            "<!DOCTYPE html><html><body>" +
            "<div style=\"width:120px;height:120px;border:30px solid #000;border-image:linear-gradient(red,blue) 30\"></div>" +
            "</body></html>");
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssBorderImageUnsupported001);
        Assert.Contains("0 0 0 rg", Latin1(result.Pdf));      // no border-image → solid border paints
    }
}
