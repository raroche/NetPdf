// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.TestKit;

namespace NetPdf.Benchmarks;

/// <summary>
/// Image-embedding benchmarks. Each method targets one image format / pipeline branch
/// so a regression localizes immediately.
/// <para>
/// <b>Honesty caveat (Task 25 review).</b> These numbers are tiny-fixture writer
/// baselines, NOT representative real-world image-embedding costs. The fixtures are
/// 1×1 / 8×8 synthetic images; their wrapper-and-emit cost is dominated by the
/// dictionary build + SHA-256 dedup hash. A full-resolution photo will measure
/// dramatically differently — JPEG passthrough scales with byte count (since the
/// passthrough hashes the payload), PNG scales with decode + re-deflate cost. Real
/// throughput characterization belongs in a Phase 4+ corpus benchmark with
/// real-world fixtures.
/// </para>
/// <para>
/// AVIF is intentionally absent. The host SkiaSharp build on macOS lacks libavif,
/// which would make the AVIF benchmark host-dependent and break the per-platform
/// pin. Phase 5 cross-platform CI will add AVIF on Linux where libavif is present.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class ImageEmbeddingBenchmarks
{
    private byte[] _jpegBytes = null!;
    private byte[] _opaquePng = null!;
    private byte[] _rgbaPng = null!;
    private byte[] _indexedPngBinaryTrns = null!;
    private byte[] _opaqueWebp = null!;
    private byte[] _transparentGif = null!;

    [GlobalSetup]
    public void Setup()
    {
        _jpegBytes = MinimalImageFixtures.MinimalBaselineJpeg();
        _opaquePng = MinimalPngFixtures.MinimalOpaqueRgb8();
        _rgbaPng = MinimalPngFixtures.MinimalRgba8();
        _indexedPngBinaryTrns = MinimalPngFixtures.MinimalIndexed8WithBinaryTrns();
        _opaqueWebp = MinimalImageFixtures.EncodeOpaqueWebp(8, 8);
        _transparentGif = MinimalImageFixtures.MinimalTransparentGif();
    }

    [Benchmark(Description = "JPEG passthrough (no decode, dictionary wrap + SHA dedup)")]
    public byte[] JpegPassthrough()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(JpegImageXObject.Build(_jpegBytes));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "PNG opaque RGB8 (Predictor 15 passthrough, no SMask)")]
    public byte[] PngOpaqueRgb8()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(PngImageXObject.Build(_opaquePng));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "PNG RGBA8 (alpha-split into Image + indirect SMask)")]
    public byte[] PngRgba8WithSMask()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(PngImageXObject.Build(_rgbaPng));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "PNG indexed8 + binary tRNS (color-key /Mask, no SMask)")]
    public byte[] PngIndexed8BinaryTrns()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(PngImageXObject.Build(_indexedPngBinaryTrns));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "WebP opaque via raster (Skia decode -> RGB FlateDecode XObject)")]
    public byte[] WebpOpaqueViaRaster()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(RasterImageXObject.Build(_opaqueWebp));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "Transparent GIF via raster (alpha-split SMask through full pipeline)")]
    public byte[] TransparentGifViaRaster()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(RasterImageXObject.Build(_transparentGif));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }
}
