// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.TestKit;

namespace NetPdf.Benchmarks;

/// <summary>
/// "Realistic-mix" multi-page document benchmark — JPEG + PNG (opaque, RGBA-with-SMask,
/// indexed-binary-tRNS) + transparent GIF + WebP, two pages with metadata. Earlier
/// drafts called this "canonical" but it does not exactly mirror the determinism
/// harness's canonical-everything document. Renamed to be honest about what it is:
/// a representative <i>mix of image embed paths</i> on a multi-page document, not a
/// 1-to-1 mirror.
/// <para>
/// The benchmark exercises every Image XObject branch in one pass — JPEG passthrough,
/// PNG opaque (Predictor 15 passthrough), PNG RGBA (alpha-split SMask), PNG indexed
/// (color-key /Mask), Skia raster decode (WebP + transparent GIF) — so a regression
/// in any single image path moves the number measurably.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class MixedDocumentBenchmark
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

    [Benchmark(Description = "Mixed JPEG + PNG (3 shapes) + WebP + transparent GIF, 2 pages")]
    public byte[] BuildMixedMultiPageDocument()
    {
        var doc = new PdfDocument
        {
            Title = "NetPdf benchmark mixed",
            Author = "NetPdf.Benchmarks",
            CreationDate = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        };

        var jpegRef = doc.RegisterImage(JpegImageXObject.Build(_jpegBytes));
        var opaquePngRef = doc.RegisterImage(PngImageXObject.Build(_opaquePng));
        var rgbaPngRef = doc.RegisterImage(PngImageXObject.Build(_rgbaPng));
        var indexedPngRef = doc.RegisterImage(PngImageXObject.Build(_indexedPngBinaryTrns));
        var webpRef = doc.RegisterImage(RasterImageXObject.Build(_opaqueWebp));
        var gifRef = doc.RegisterImage(RasterImageXObject.Build(_transparentGif));

        var p1 = doc.AddPage(MediaBoxSize.A4);
        p1.AppendContent("0.95 0.95 0.95 rg 0 0 595 842 re f\n");
        p1.PlaceImage(jpegRef, 50, 50, 100, 100);
        p1.PlaceImage(opaquePngRef, 200, 50, 100, 100);
        p1.PlaceImage(rgbaPngRef, 350, 50, 100, 100);

        var p2 = doc.AddPage(MediaBoxSize.Letter);
        p2.AppendContent("0 0 0 rg 0 0 612 792 re S\n");
        p2.PlaceImage(indexedPngRef, 100, 100, 80, 80);
        p2.PlaceImage(webpRef, 200, 100, 80, 80);
        p2.PlaceImage(gifRef, 300, 100, 80, 80);
        p2.PlaceImage(jpegRef, 100, 400, 80, 80); // dedup hit
        return doc.Save();
    }
}
