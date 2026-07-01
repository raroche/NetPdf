// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.RenderingCorpus.Visual;
using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 — the PDF-rasterizer seam. The PDFium backend (via <c>PDFtoImage</c>) is now wired,
/// so the factory returns a working rasterizer once the native-load probe succeeds; the placeholder still
/// throws a clear message where a rasterizer is structurally required but none configured. The interface
/// round-trips (a fake + the real PDFium adapter both satisfy it).</summary>
[Collection(PdfiumCollection.Name)]
public sealed class PdfRasterizerTests
{
    [Fact]
    public void TryCreateDefault_returns_the_wired_pdfium_rasterizer()
    {
        var ok = PdfRasterizers.TryCreateDefault(out var rasterizer, out var reason);
        // PDFium ships native assets for macOS/Linux/Windows, so the probe should succeed on our targets. If a
        // platform ever lacks the native lib, the factory reports unavailable with a reason (the runner skips).
        if (ok)
        {
            Assert.NotNull(rasterizer);
            Assert.True(string.IsNullOrEmpty(reason));
        }
        else
        {
            Assert.Null(rasterizer);
            Assert.Contains("PDFium", reason);
        }
    }

    [Fact]
    public void Pdfium_rasterizer_renders_a_netpdf_pdf_to_rgba_pages()
    {
        // The real backend: render a small NetPdf PDF → per-page RGBA rasters at the harness DPI. A blue box
        // on white proves it decoded actual page content (not a blank/placeholder).
        var pdf = NetPdf.HtmlPdf.Convert("<html><body style=\"margin:0\"><div style=\"width:120px;height:80px;background:blue\"></div></body></html>");
        var pages = new PdfiumRasterizer().RasterizeAllPages(pdf, VisualHarness.Dpi);
        Assert.Single(pages);
        var page = pages[0];
        page.EnsureValid();
        Assert.True(page.Width > 100 && page.Height > 100); // an A4-ish raster at 300 DPI
        var (blue, white) = (0, 0);
        for (var i = 0; i < page.Rgba.Length; i += 4)
        {
            var (r, g, b) = (page.Rgba[i], page.Rgba[i + 1], page.Rgba[i + 2]);
            if (b > 150 && r < 90 && g < 90) blue++;
            else if (r > 220 && g > 220 && b > 220) white++;
        }
        Assert.True(blue > 100);   // the blue box rendered
        Assert.True(white > blue); // on a mostly-white page
    }

    [Fact]
    public void Pdfium_rasterizer_renders_every_page_of_a_multi_page_pdf()
    {
        // The multi-page contract (page-for-page visual regression): a forced page break yields 2 pages, and
        // the adapter must return BOTH — sized, and with DISTINCT content (blue on page 1, red on page 2).
        // Guards against a page-indexing / PDFtoImage regression dropping or duplicating later pages.
        var pdf = NetPdf.HtmlPdf.Convert(
            "<html><body style=\"margin:0\">" +
            "<div style=\"width:200px;height:120px;background:blue\"></div>" +
            "<div style=\"break-before:page;width:200px;height:120px;background:red\"></div>" +
            "</body></html>");
        var pages = new PdfiumRasterizer().RasterizeAllPages(pdf, dpi: 96);
        Assert.True(pages.Count >= 2, $"expected 2+ pages from the forced break, got {pages.Count}");
        foreach (var p in pages)
        {
            p.EnsureValid();
            Assert.True(p.Width > 50 && p.Height > 50);
        }
        Assert.True(CountColor(pages[0], red: false) > 100);          // page 1: the blue box
        Assert.True(CountColor(pages[1], red: true) > 100);           // page 2: the red box (distinct content)
        Assert.True(CountColor(pages[0], red: true) < CountColor(pages[1], red: true)); // pages differ
    }

    private static int CountColor(RasterImage img, bool red)
    {
        var n = 0;
        for (var i = 0; i < img.Rgba.Length; i += 4)
        {
            var (r, g, b) = (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
            if (red ? (r > 150 && g < 90 && b < 90) : (b > 150 && r < 90 && g < 90)) n++;
        }
        return n;
    }

    [Fact]
    public void Pdfium_rasterization_of_the_same_pdf_is_pixel_identical_to_itself()
    {
        // End-to-end pipeline check with the now-live backend: rasterize the SAME PDF twice and PixelDiff →
        // zero delta / SSIM 1. Exercises rasterize → RasterImage → PixelDiff without needing a Chrome reference.
        var pdf = NetPdf.HtmlPdf.Convert(VisualHarness.ReadInvoiceHtml(VisualHarness.DiffableInvoices[0]));
        var r = new PdfiumRasterizer();
        var a = r.RasterizeAllPages(pdf, VisualHarness.Dpi);
        var b = r.RasterizeAllPages(pdf, VisualHarness.Dpi);
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            var diff = PixelDiff.Compare(a[i], b[i]);
            Assert.Equal(0, diff.MaxChannelDelta);
            Assert.Equal(1.0, diff.Ssim, 3);
        }
    }

    [Fact]
    public void NotConfigured_rasterizer_throws_a_clear_message()
    {
        var ex = Assert.Throws<PdfRasterizationUnavailableException>(
            () => PdfRasterizers.NotConfigured.RasterizeAllPages([1, 2, 3], 300));
        Assert.Contains("PDFium", ex.Message);
    }

    [Fact]
    public void A_fake_multi_page_rasterizer_satisfies_the_interface()
    {
        IPdfRasterizer fake = new FakeRasterizer();
        var pages = fake.RasterizeAllPages([0x25, 0x50, 0x44, 0x46], dpi: 300); // "%PDF"
        Assert.Equal(2, pages.Count);                 // a multi-page seam, not first-page-only
        foreach (var page in pages)
        {
            Assert.Equal(2, page.Width);
            Assert.Equal(2, page.Height);
            page.EnsureValid();
        }
    }

    private sealed class FakeRasterizer : IPdfRasterizer
    {
        public System.Collections.Generic.IReadOnlyList<RasterImage> RasterizeAllPages(byte[] pdf, int dpi) =>
            [new(2, 2, new byte[2 * 2 * 4]), new(2, 2, new byte[2 * 2 * 4])];
    }
}
