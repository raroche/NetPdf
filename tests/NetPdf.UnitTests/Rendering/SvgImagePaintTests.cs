// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 SVG part 1 (PR 5) — end-to-end: an <c>&lt;img&gt;</c> whose source is an SVG document
/// (data: URI) is rasterized by NetPdf.Svg and placed as an image XObject (<c>Do</c>) through the existing
/// image pipeline. Page content is uncompressed.</summary>
public sealed class SvgImagePaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string SvgDataUri(string svg) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

    [Fact]
    public void Svg_image_is_rasterized_and_placed()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\">" +
            "<rect x=\"0\" y=\"0\" width=\"40\" height=\"40\" fill=\"#3366cc\"/>" +
            "<circle cx=\"20\" cy=\"20\" r=\"10\" fill=\"red\"/></svg>";
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{SvgDataUri(svg)}\" style=\"width:80px;height:80px\">" +
            "</body></html>"));
        Assert.Contains("Do", pdf);                 // the rasterized SVG is placed as an XObject
        Assert.Contains("/Subtype /Image", pdf);    // it embedded as an image
    }

    [Fact]
    public void Svg_with_path_and_transform_renders()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"50\" height=\"50\">" +
            "<g transform=\"translate(10,10)\"><path d=\"M0 0 L30 0 L15 30 Z\" fill=\"green\"/></g></svg>";
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            $"<img src=\"{SvgDataUri(svg)}\" style=\"width:50px;height:50px\">" +
            "</body></html>"));
        Assert.Contains("Do", pdf);
    }
}
