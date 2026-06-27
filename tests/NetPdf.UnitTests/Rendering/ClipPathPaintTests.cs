// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf;
using NetPdf.Diagnostics;
using NetPdf.UnitTests.Pdf.Images;
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
    public void Polygon_evenodd_clips_with_the_even_odd_operator()
    {
        var text = Latin1(HtmlPdf.Convert(Html("polygon(evenodd, 50% 0%, 100% 100%, 0% 100%)")));
        Assert.Contains("W* n", text);            // even-odd clip rule
        Assert.Contains("1 0 0 rg", text);
    }

    [Fact]
    public void Polygon_nonzero_clips_with_the_nonzero_operator()
    {
        var text = Latin1(HtmlPdf.Convert(Html("polygon(50% 0%, 100% 100%, 0% 100%)")));
        Assert.Contains("W n", text);
        Assert.DoesNotContain("W* n", text);      // default is nonzero, not even-odd
    }

    [Fact]
    public void Circle_closest_side_keyword_clips()
    {
        // circle(closest-side) is explicitly valid (CSS Shapes §funcdef-basic-shape-circle) — it must
        // clip (an ellipse Bézier path), not surface the unsupported diagnostic (PR #228 review P3).
        var result = HtmlPdf.ConvertDetailed(Html("circle(closest-side)"));
        Assert.Contains("W n", Latin1(result.Pdf));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathUnsupported001);
    }

    [Fact]
    public void Url_reference_clip_path_warns_unsupported_and_paints_unclipped()
    {
        // PR #228 review P2 — a url(#clip) SVG-reference clip can't be applied natively; it must warn
        // (never drop content silently) and paint unclipped.
        var result = HtmlPdf.ConvertDetailed(Html("url(#myclip)"));
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathUnsupported001);
        Assert.Contains("1 0 0 rg", Latin1(result.Pdf)); // still painted (unclipped)
    }

    [Fact]
    public void Malformed_basic_shape_clip_path_warns_unsupported()
    {
        var result = HtmlPdf.ConvertDetailed(Html("polygon(0% 0%)")); // < 3 vertices
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathUnsupported001);
    }

    [Fact]
    public void Path_clip_is_now_applied_natively()
    {
        // clip-path: path("…") now clips natively (an SVG-path → PDF path + W n), no raster-fallback diagnostic.
        var result = HtmlPdf.ConvertDetailed(Html("path('M0 0 L100 0 L50 100 Z')"));
        var text = Latin1(result.Pdf);
        Assert.Contains("W n", text);                 // the path clip
        Assert.Contains("1 0 0 rg", text);            // the clipped red background still fills
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathRasterFallback001);
    }

    [Fact]
    public void Path_clip_with_curves_emits_bezier_segments()
    {
        // A cubic in the path data → a `c` operator in the clip path (curves are preserved, not flattened).
        var text = Latin1(HtmlPdf.Convert(Html("path('M10 10 C 10 50 90 50 90 10 Z')")));
        Assert.Contains(" c ", text);                 // cubic clip segment
        Assert.Contains("W n", text);
    }

    [Fact]
    public void Path_clip_evenodd_uses_the_even_odd_operator()
    {
        var text = Latin1(HtmlPdf.Convert(Html("path(evenodd, 'M0 0 L100 0 L100 100 L0 100 Z M25 25 L75 25 L75 75 L25 75 Z')")));
        Assert.Contains("W* n", text);                // even-odd clip rule (a hole)
    }

    [Fact]
    public void Path_clip_nonzero_is_the_default()
    {
        var text = Latin1(HtmlPdf.Convert(Html("path('M0 0 L100 0 L50 100 Z')")));
        Assert.Contains("W n", text);
        Assert.DoesNotContain("W* n", text);
    }

    [Fact]
    public void Path_clip_on_an_img_clips_the_image()
    {
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(16, 16));
        var html = "<!DOCTYPE html><html><body>" +
            $"<img src=\"{dataUri}\" style=\"width:64px;height:64px;clip-path:path('M0 0 L64 0 L32 64 Z')\">" +
            "</body></html>";
        var text = Latin1(HtmlPdf.Convert(html));
        Assert.Contains("W n", text);                 // the path clip wraps the image
        Assert.Contains("Do", text);                  // the image draws inside the clip
    }

    [Fact]
    public void Inset_round_uses_per_corner_radii()
    {
        // inset() with 4 distinct corner radii → a rounded clip whose corners differ (not a single uniform
        // radius). The rounded clip emits four Bézier corner arcs.
        var text = Latin1(HtmlPdf.Convert(Html("inset(10px round 4px 8px 12px 16px)")));
        Assert.Contains("W n", text);
        Assert.Contains(" c ", text);                 // rounded corners (Bézier arcs)
        Assert.Contains("1 0 0 rg", text);
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
    public void Clip_path_on_an_img_clips_the_image()
    {
        // The most common clip-path use: clipping a photo to a shape. The image (Do) is wrapped in
        // the ellipse clip; the box is a leaf, so no subtree warning.
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(SyntheticRasterImage.BuildOpaquePng(16, 16));
        var html = "<!DOCTYPE html><html><body>" +
            $"<img src=\"{dataUri}\" style=\"width:64px;height:64px;clip-path:circle(30px)\">" +
            "</body></html>";
        var result = HtmlPdf.ConvertDetailed(html);
        var text = Latin1(result.Pdf);
        Assert.Contains("W n", text);
        Assert.Contains("Do", text);          // the image is drawn (inside the clip)
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathSubtreeUnsupported001);
    }

    [Fact]
    public void No_clip_path_emits_no_clip_diagnostics()
    {
        var result = HtmlPdf.ConvertDetailed(Html("none"));
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathRasterFallback001);
        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.CssClipPathSubtreeUnsupported001);
    }
}
