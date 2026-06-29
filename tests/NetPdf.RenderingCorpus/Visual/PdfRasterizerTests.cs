// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.RenderingCorpus.Visual;
using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 — the PDF-rasterizer seam. No native backend is wired yet, so the factory reports
/// unavailable (the runner skips) and the placeholder throws a clear message rather than crashing. The
/// interface is usable (a fake implementation compiles + round-trips), proving the seam is sound for when
/// the maintainer installs PDFium.</summary>
public sealed class PdfRasterizerTests
{
    [Fact]
    public void TryCreateDefault_reports_unavailable_with_a_reason()
    {
        var ok = PdfRasterizers.TryCreateDefault(out var rasterizer, out var reason);
        Assert.False(ok);
        Assert.Null(rasterizer);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("PDFium", reason);
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
