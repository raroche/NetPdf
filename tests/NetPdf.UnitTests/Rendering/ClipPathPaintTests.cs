// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 clip-path (PR 3) — end-to-end: a <c>clip-path</c> basic shape wraps the box's
/// decoration in a native PDF clip (a <c>W n</c> path before the fill); <c>path()</c> + a clipped
/// element with children surface their deferral diagnostics. Page content is uncompressed.</summary>
public sealed class ClipPathPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Html(string clipPath) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:100px;height:100px;background:red;clip-path:{clipPath}\"></div>" +
        "</body></html>";

    [Fact]
    public void Inset_clips_the_decoration_to_a_rectangle()
    {
        var text = Latin1(HtmlPdf.Convert(Html("inset(10px)")));
        Assert.Contains("re\nW n", text.Replace(" W n", "\nW n")); // a rect clip path + W n
        Assert.Contains("1 0 0 rg", text);   // the (clipped) red background still fills
    }

    [Fact]
    public void Circle_clips_with_an_ellipse_bezier_path()
    {
        var text = Latin1(HtmlPdf.Convert(Html("circle(40px)")));
        Assert.Contains("W n", text);
        Assert.Contains("c\n", text.Replace(" c ", " c\n")); // the ellipse Bézier curves
        Assert.Contains("1 0 0 rg", text);
    }

    [Fact]
    public void Polygon_clips_with_a_line_path()
    {
        var text = Latin1(HtmlPdf.Convert(Html("polygon(50% 0%, 100% 100%, 0% 100%)")));
        Assert.Contains("W n", text);
        Assert.Contains("1 0 0 rg", text);
    }

    [Fact]
    public void Path_clip_is_deferred_with_a_diagnostic_and_paints_unclipped()
    {
        var result = HtmlPdf.ConvertDetailed(Html("path('M0 0 L100 0 L50 100 Z')"));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathRasterFallback001);
        Assert.Contains("1 0 0 rg", Latin1(result.Pdf)); // still painted (unclipped)
    }

    [Fact]
    public void Clip_path_on_an_element_with_children_warns_about_the_subtree()
    {
        var html = "<!DOCTYPE html><html><body>" +
            "<div style=\"width:100px;height:100px;clip-path:circle(40px)\"><span>hi</span></div>" +
            "</body></html>";
        var result = HtmlPdf.ConvertDetailed(html);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathSubtreeUnsupported001);
    }

    [Fact]
    public void No_clip_path_emits_no_clip_diagnostics()
    {
        var result = HtmlPdf.ConvertDetailed(Html("none"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathRasterFallback001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathSubtreeUnsupported001);
    }
}
