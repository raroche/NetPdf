// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using SkiaSharp;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>The PDFium-backed <see cref="IPdfRasterizer"/> (PR 8). PDFium reads the PDF and renders each page
/// to a bitmap — SkiaSharp only WRITES PDF, so the NetPdf-side (and reference-side) PDF→raster goes through
/// PDFium via <c>PDFtoImage</c>. Every page renders at the target DPI into an RGBA <see cref="RasterImage"/>
/// (the harness's common currency), read via <see cref="SKBitmap.Pixels"/> so it's independent of PDFium's
/// native pixel order.</summary>
internal sealed class PdfiumRasterizer : IPdfRasterizer
{
#pragma warning disable CA1416 // PDFtoImage is annotated per-OS; PDFium ships native assets for macOS/Linux/Win — all our targets.
    public IReadOnlyList<RasterImage> RasterizeAllPages(byte[] pdf, int dpi)
    {
        var count = PDFtoImage.Conversion.GetPageCount(pdf);
        var pages = new List<RasterImage>(count);
        for (var i = 0; i < count; i++)
        {
            using var bmp = PDFtoImage.Conversion.ToImage(pdf, page: i, password: null,
                options: new PDFtoImage.RenderOptions(Dpi: dpi));
            pages.Add(ToRasterImage(bmp));
        }
        return pages;
    }
#pragma warning restore CA1416

    private static RasterImage ToRasterImage(SKBitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var rgba = new byte[w * h * 4];
        var pixels = bmp.Pixels; // SKColor[] — R/G/B/A accessors, independent of the native storage order
        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            var o = i * 4;
            rgba[o] = c.Red;
            rgba[o + 1] = c.Green;
            rgba[o + 2] = c.Blue;
            rgba[o + 3] = c.Alpha;
        }
        var image = new RasterImage(w, h, rgba);
        image.EnsureValid();
        return image;
    }
}
