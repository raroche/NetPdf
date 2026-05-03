// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;

namespace NetPdf.Benchmarks;

/// <summary>
/// Phase 1 baseline measurements over the byte-writer hot path. Numbers established here
/// become the reference for regression detection — when a Phase 2/3 layout change ships,
/// rerunning this suite tells us whether anything in the writer regressed even though
/// nothing in <c>NetPdf.Pdf</c> changed (e.g., a new dictionary entry on every page,
/// an extra hash pass over content streams, a different default compression level).
/// <para>
/// Each method covers a single dimension of the surface so a regression points at the
/// affected subsystem. <see cref="MemoryDiagnoser"/> is on so allocations / Gen0 counts
/// are reported alongside wall-clock time — the "memory grows linearly with page count"
/// invariant from the Phase 1 spec is enforced by inspecting allocations on the
/// page-count theory benchmark.
/// </para>
/// <para>
/// Run with the default short config: <c>dotnet run -c Release --project tests/NetPdf.Benchmarks
/// -- --filter "*"</c>. For a fast iteration loop use <c>--filter "*Pages*"</c> or
/// similar globs.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PdfWriteBenchmarks
{
    // Pre-built byte arrays so the benchmark's hot path measures the writer, not the
    // synthetic-fixture builder. Allocated once in [GlobalSetup].
    private byte[] _jpegBytes = null!;
    private byte[] _transparentGifBytes = null!;

    [Params(1, 10, 100)]
    public int PageCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _jpegBytes = BuildMinimalBaselineJpeg();
        _transparentGifBytes = BuildMinimalTransparentGif();
    }

    // ───── Minimal write path ────────────────────────────────────────────────

    [Benchmark(Description = "Single blank A4 page → bytes")]
    public byte[] BlankSinglePage()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    // ───── Page-count linearity (memory invariant) ───────────────────────────

    [Benchmark(Description = "N blank pages → bytes (memory should grow ~linearly)")]
    public byte[] BlankPages_ParameterizedCount()
    {
        var doc = new PdfDocument();
        for (var i = 0; i < PageCount; i++)
        {
            doc.AddPage(MediaBoxSize.A4);
        }
        return doc.Save();
    }

    [Benchmark(Description = "N pages with simple content stream → bytes")]
    public byte[] PagesWithSimpleContent_ParameterizedCount()
    {
        var doc = new PdfDocument();
        for (var i = 0; i < PageCount; i++)
        {
            var page = doc.AddPage(MediaBoxSize.A4);
            page.AppendContent("0.5 0.5 0.5 rg 50 50 100 100 re f\n");
            page.AppendContent("0 0 0 rg 200 200 50 50 re S\n");
        }
        return doc.Save();
    }

    // ───── Image embedding — single dimension per benchmark ──────────────────

    [Benchmark(Description = "JPEG passthrough (single image, single page)")]
    public byte[] JpegPassthrough()
    {
        var doc = new PdfDocument();
        var jpeg = JpegImageXObject.Build(_jpegBytes);
        var imageRef = doc.RegisterImage(jpeg);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "Transparent GIF via raster (alpha-split SMask path)")]
    public byte[] TransparentGifRaster()
    {
        var doc = new PdfDocument();
        var raster = RasterImageXObject.Build(_transparentGifBytes);
        var imageRef = doc.RegisterImage(raster);
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "Image dedup: register same JPEG 100x (cache hit cost)")]
    public byte[] ImageDedupCacheHits()
    {
        var doc = new PdfDocument();
        var jpeg = JpegImageXObject.Build(_jpegBytes);
        var firstRef = doc.RegisterImage(jpeg);
        for (var i = 0; i < 99; i++)
        {
            // Each subsequent call hashes the dict + payload and hits the cache.
            // Should be O(1) per call (not O(N)) and produce zero allocations
            // beyond the SHA-256 hashing.
            var dedupRef = doc.RegisterImage(jpeg);
            if (dedupRef.ObjectNumber != firstRef.ObjectNumber)
            {
                throw new InvalidOperationException("Dedup regression.");
            }
        }
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PlaceImage(firstRef, 50, 50, 100, 100);
        return doc.Save();
    }

    // ───── Canonical "everything-in" document ────────────────────────────────

    [Benchmark(Description = "Canonical multi-page document (matches AOT smoke + determinism harness)")]
    public byte[] CanonicalEverythingDocument()
    {
        var doc = new PdfDocument
        {
            Title = "NetPdf benchmark canonical",
            Author = "NetPdf.Benchmarks",
            CreationDate = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        };

        var jpeg = JpegImageXObject.Build(_jpegBytes);
        var jpegRef = doc.RegisterImage(jpeg);
        var rasterResult = RasterImageXObject.Build(_transparentGifBytes);
        var rasterRef = doc.RegisterImage(rasterResult);

        var p1 = doc.AddPage(MediaBoxSize.A4);
        p1.AppendContent("0.95 0.95 0.95 rg 0 0 595 842 re f\n");
        p1.PlaceImage(jpegRef, 50, 50, 100, 100);
        p1.PlaceImage(rasterRef, 200, 50, 100, 100);

        var p2 = doc.AddPage(MediaBoxSize.Letter);
        p2.AppendContent("0 0 0 rg 0 0 612 792 re S\n");
        p2.PlaceImage(jpegRef, 100, 100, 80, 80);

        return doc.Save();
    }

    // ───── Synthetic fixtures (kept inline so the benchmark project has zero
    //       dependency on test fixtures) ────────────────────────────────────

    private static byte[] BuildMinimalBaselineJpeg() =>
    [
        0xFF, 0xD8,                                         // SOI
        0xFF, 0xE0, 0x00, 0x10,                             // APP0 length 16
        (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
        0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        // SOF0: precision=8, height=1, width=1, components=3
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03,
        0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        // SOS
        0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
        0x00,
        0xFF, 0xD9, // EOI
    ];

    private static byte[] BuildMinimalTransparentGif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,
        0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        0x3B,
    ];
}
