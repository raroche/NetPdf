// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Paint;

/// <summary>
/// PAINT-PROOF assertions at the PDF-operator level. The W3C conformance suite
/// (<c>tests/NetPdf.W3cConformance/</c>) proves spec-correct LAYOUT geometry, but — as the release review
/// noted — it cannot prove that a background actually FILLS, a border RENDERS, a transform APPLIES, or a
/// gradient PAINTS, because those are paint-stage effects with no layout-box footprint. These tests close
/// that gap directly: the facade emits an UNCOMPRESSED content stream (a determinism guarantee — see the
/// project test-technique notes), so the exact PDF operators are string-searchable. Each case renders a
/// tiny document and asserts the distinctive operator + operand the feature must emit (a chosen-distinct
/// color or matrix value that could not appear by accident), so a regression that silently stops painting
/// a feature fails here.
/// </summary>
public sealed class PaintOperatorTests
{
    private static string RenderContent(string body) =>
        Encoding.Latin1.GetString(
            HtmlPdf.Convert($"<!DOCTYPE html><html><body style=\"margin:0\">{body}</body></html>"));

    [Fact]
    public void Background_color_emits_a_filled_rectangle_in_that_color()
    {
        // #3366cc = 51/102/204 over 255 = 0.2 0.4 0.8 — a device-RGB fill (rg) of a rectangle (re f).
        var pdf = RenderContent("<div style='width:100px;height:50px;background:#3366cc'></div>");
        Assert.Contains("0.2 0.4 0.8 rg", pdf);   // the background color reached the page as a fill color
        Assert.Contains("re f", pdf);              // …painting a filled rectangle

        // Negative control: no background ⇒ that fill color is NOT emitted (the assertion isn't vacuous).
        var plain = RenderContent("<div style='width:100px;height:50px'></div>");
        Assert.DoesNotContain("0.2 0.4 0.8 rg", plain);
    }

    [Fact]
    public void Border_renders_as_a_filled_edge_in_the_border_color()
    {
        // #cc0000 = 0.8 0 0. A solid border paints its edges as filled rects in the border color.
        var pdf = RenderContent("<div style='width:100px;height:50px;border:5px solid #cc0000'></div>");
        Assert.Contains("0.8 0 0 rg", pdf);
        Assert.Contains("re f", pdf);
    }

    [Fact]
    public void Transform_rotate_emits_a_non_identity_cm_matrix()
    {
        // rotate(30deg) → a PDF `cm` with cos30 ≈ 0.86603 and the ∓sin30 = ∓0.5 off-diagonal terms; an
        // identity or translation-only matrix could never produce those, so this proves the rotation applied.
        var pdf = RenderContent("<div style='width:80px;height:40px;background:#ddd;transform:rotate(30deg)'></div>");
        Assert.Contains("0.86603", pdf);
        Assert.Contains("0.5 0.86603", pdf);  // the sin/cos off-diagonal of the rotation matrix
        Assert.Contains(" cm", pdf);
    }

    [Fact]
    public void Linear_gradient_emits_a_native_pdf_shading()
    {
        // A native axial shading is a `/Sh<n> sh` paint inside a clip — proves the gradient is drawn as a
        // real PDF shading (ShadingType 2), not silently dropped or flattened to a solid fill.
        var pdf = RenderContent("<div style='width:100px;height:50px;background:linear-gradient(90deg,#f00,#00f)'></div>");
        Assert.Contains("/Sh", pdf);   // a shading resource is referenced
        Assert.Contains(" sh", pdf);   // …and painted with the `sh` operator
    }

    [Fact]
    public void Radial_gradient_emits_a_native_pdf_shading()
    {
        var pdf = RenderContent("<div style='width:100px;height:50px;background:radial-gradient(#f00,#00f)'></div>");
        Assert.Contains("/Sh", pdf);
        Assert.Contains(" sh", pdf);
    }
}
