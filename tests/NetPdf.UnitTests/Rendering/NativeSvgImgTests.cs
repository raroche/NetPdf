// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 native vector SVG (opt-in) — with <see cref="HtmlPdfOptions.NativeSvgRendering"/> on, a
/// supported inline-data SVG <c>&lt;img&gt;</c> renders as native PDF path ops (no image XObject); off (the
/// default) it rasterizes exactly as before (byte-identical). Content is uncompressed → string-inspectable.</summary>
public sealed class NativeSvgImgTests
{
    private static string DataImg(string svg)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return "<!DOCTYPE html><html><body>" +
            $"<img src=\"data:image/svg+xml;base64,{b64}\" style=\"width:72px;height:72px\"/>" +
            "</body></html>";
    }

    private const string RedRectSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
        "<rect x='0' y='0' width='100' height='100' fill='#ff0000'/></svg>";

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void Native_svg_on_emits_vector_ops_and_no_image_xobject()
    {
        var pdf = Latin1(HtmlPdf.Convert(DataImg(RedRectSvg),
            new HtmlPdfOptions { NativeSvgRendering = true }));
        Assert.Contains("1 0 0 rg", pdf);        // native red fill
        Assert.Contains(" f Q", pdf);            // native fill op
        Assert.DoesNotContain("/Subtype /Image", pdf); // no rasterized XObject placed
    }

    [Fact]
    public void Native_svg_off_rasterizes_as_before()
    {
        var pdf = Latin1(HtmlPdf.Convert(DataImg(RedRectSvg)));
        Assert.Contains("/Subtype /Image", pdf); // the rasterized SVG XObject (default path)
    }

    [Fact]
    public void Unsupported_svg_falls_back_to_raster_even_when_native_on()
    {
        // A <text> SVG is outside the native subset → the emitter bails, so the raster XObject is placed.
        var textSvg =
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<text x='10' y='50'>hi</text></svg>";
        var pdf = Latin1(HtmlPdf.Convert(DataImg(textSvg),
            new HtmlPdfOptions { NativeSvgRendering = true }));
        Assert.Contains("/Subtype /Image", pdf); // fell back to raster
    }
}
