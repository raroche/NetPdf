// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 gradients (PR 1 refinements) — radial-gradient ELLIPSE shaping. The default
/// (ellipse) shape on a non-square box now renders a true ellipse by registering a circle and
/// squashing one axis with a CTM (a <c>cm</c> emitted between the clip and the <c>sh</c>); a circle,
/// or a centered ellipse on a square box (rx == ry), emits NO CTM — byte-identical with the
/// pre-ellipse circular output. Page content is uncompressed.</summary>
public sealed class RadialEllipseGradientPaintTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Html(int w, int h, string gradient) =>
        "<!DOCTYPE html><html><body>" +
        $"<div style=\"width:{w}px;height:{h}px;background-image:{gradient}\"></div>" +
        "</body></html>";

    [Fact]
    public void Default_ellipse_on_a_non_square_box_emits_a_squashing_ctm()
    {
        var text = Latin1(HtmlPdf.Convert(Html(120, 40, "radial-gradient(red, blue)")));
        Assert.Contains("/ShadingType 3", text);  // native radial shading
        Assert.Contains("cm /Sh", text);          // the CTM precedes the shading paint → an ellipse
    }

    [Fact]
    public void Circle_keyword_emits_no_ctm()
    {
        var text = Latin1(HtmlPdf.Convert(Html(120, 40, "radial-gradient(circle, red, blue)")));
        Assert.Contains("/ShadingType 3", text);
        Assert.DoesNotContain("cm /Sh", text);     // a circle needs no axis squash
    }

    [Fact]
    public void Centered_ellipse_on_a_square_box_emits_no_ctm()
    {
        // rx == ry on a square box → the ellipse degenerates to a circle → byte-identical (no CTM).
        var text = Latin1(HtmlPdf.Convert(Html(80, 80, "radial-gradient(red, blue)")));
        Assert.Contains("/ShadingType 3", text);
        Assert.DoesNotContain("cm /Sh", text);
    }

    [Fact]
    public void Ellipse_closest_side_on_a_non_square_box_emits_a_ctm()
    {
        var text = Latin1(HtmlPdf.Convert(Html(120, 40, "radial-gradient(ellipse closest-side, red, blue)")));
        Assert.Contains("cm /Sh", text);
    }
}
