// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf;
using NetPdf.Svg;
using Xunit;

namespace NetPdf.UnitTests.Svg;

/// <summary>Phase 4 native vector SVG → PDF — <see cref="SvgNativeEmitter.TryEmit"/> draws a supported SVG as
/// native PDF path operators onto a page (crisp at any zoom), and returns <see langword="false"/> for anything
/// outside the tractable subset (so the caller falls back to the raster path). The page content is
/// uncompressed → the operators are string-inspectable.</summary>
public sealed class SvgNativeEmitterTests
{
    private static byte[] Bytes(string svg) => Encoding.UTF8.GetBytes(svg);

    private static (bool Ok, bool Unsupported, string Content) Emit(string svg)
    {
        var page = new PdfDocument().AddPage(MediaBoxSize.A4);
        var ok = SvgNativeEmitter.TryEmit(Bytes(svg), page, 100, 200, 72, 72, out var unsupported);
        var (_, content) = page.Finalize();
        return (ok, unsupported, Encoding.ASCII.GetString(content));
    }

    [Fact]
    public void A_filled_rect_emits_native_fill_ops()
    {
        var (ok, unsupported, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<rect x='0' y='0' width='100' height='100' fill='#ff0000'/></svg>");
        Assert.True(ok);
        Assert.False(unsupported);
        Assert.Contains("1 0 0 rg", content); // red fill
        Assert.Contains(" m ", content);       // a path was constructed
        Assert.Contains(" f Q", content);      // nonzero fill
    }

    [Fact]
    public void ViewBox_maps_into_the_target_rect_in_pdf_points()
    {
        // 100-unit viewBox → a 72pt box at (100,200). With xMidYMid meet the scale is 72/100 = 0.72, so the
        // rect's top-left SVG (0,0) lands at PDF y = 200 + 72 = 272 and x = 100.
        var (ok, _, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<rect x='0' y='0' width='100' height='100' fill='#000'/></svg>");
        Assert.True(ok);
        Assert.Contains("100 272 m", content); // top-left corner mapped + Y-flipped
    }

    [Fact]
    public void A_stroked_circle_emits_native_stroke_ops()
    {
        var (ok, _, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<circle cx='50' cy='50' r='40' fill='none' stroke='#0000ff' stroke-width='5'/></svg>");
        Assert.True(ok);
        Assert.Contains("0 0 1 RG", content); // blue stroke
        Assert.Contains(" c ", content);       // circle → cubic curves
        Assert.Contains("S Q", content);       // stroked
        // stroke-width 5 in a 100-unit box scaled 0.72 → 3.6pt
        Assert.Contains("3.6 w", content);
    }

    [Fact]
    public void Group_transform_composes_onto_child_geometry()
    {
        var (ok, _, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<g transform='translate(10,0)'><rect x='0' y='0' width='10' height='10' fill='#000'/></g></svg>");
        Assert.True(ok);
        Assert.Contains(" m ", content); // rendered (translate applied, no crash)
    }

    [Fact]
    public void Fill_rule_evenodd_selects_f_star()
    {
        var (ok, _, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<path d='M10 10 H 90 V 90 H 10 Z' fill='#000' fill-rule='evenodd'/></svg>");
        Assert.True(ok);
        Assert.Contains("f* Q", content);
    }

    [Fact]
    public void A_gradient_paint_server_falls_back_to_raster()
    {
        var (ok, unsupported, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<defs><linearGradient id='g'><stop offset='0' stop-color='red'/></linearGradient></defs>" +
            "<rect x='0' y='0' width='100' height='100' fill='url(#g)'/></svg>");
        Assert.False(ok);            // caller falls back to raster
        Assert.True(unsupported);
        Assert.DoesNotContain(" f Q", content); // page untouched — no partial native ops committed
    }

    [Fact]
    public void Text_falls_back_to_raster()
    {
        var (ok, unsupported, _) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<text x='10' y='50'>hi</text></svg>");
        Assert.False(ok);
        Assert.True(unsupported);
    }

    [Fact]
    public void Unsupported_document_leaves_the_page_untouched()
    {
        // A supported rect FOLLOWED by unsupported text → all-or-nothing: nothing is committed.
        var (ok, _, content) = Emit(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'>" +
            "<rect x='0' y='0' width='50' height='50' fill='#000'/>" +
            "<text x='10' y='50'>hi</text></svg>");
        Assert.False(ok);
        Assert.DoesNotContain(" f Q", content); // the rect was NOT drawn (buffered, then discarded)
    }
}
