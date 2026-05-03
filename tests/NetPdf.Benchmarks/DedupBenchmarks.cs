// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using NetPdf.TestKit;

namespace NetPdf.Benchmarks;

/// <summary>
/// Image-cache dedup benchmarks. Splits the workload into three concepts so the
/// per-call cost claim is measurable, not inferred from a mixed workload (Task 25
/// review feedback):
/// <list type="bullet">
///   <item><see cref="FirstRegistration_AndSave"/> — full first-time cost: build
///         the image, hash it, allocate the indirect-object slot, run the rest of
///         the document save. The number consumers care about for "embedding one
///         image."</item>
///   <item><see cref="CacheHits_Isolated"/> — the inner-loop cost of subsequent
///         registrations that hit the cache (SHA-256 of payload + dict, dict
///         lookup, return existing ref). Uses
///         <see cref="BenchmarkAttribute.OperationsPerInvoke"/> to amortize across
///         99 calls so per-call cost is reported directly. The Save() step and
///         first-registration step are EXCLUDED from the timed region — measured in
///         <see cref="FirstRegistration_AndSave"/>.</item>
///   <item><see cref="CacheMisses_100UniqueImages"/> — registers 100 distinct
///         images (vary one byte each) so every call is a cache miss + indirect
///         allocation. Catches a regression where dictionary growth is O(N²) on
///         unique inserts.</item>
/// </list>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class DedupBenchmarks
{
    private byte[] _jpegBytes = null!;

    // Iteration-scoped state for CacheHits_Isolated: a fresh PdfDocument with the
    // image registered once. Re-built per iteration so the cache state is uniform.
    private PdfDocument _docWithFirstRegistration = null!;
    private PdfStream _jpegStream = null!;
    private byte[][] _uniqueJpegBytes = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _jpegBytes = MinimalImageFixtures.MinimalBaselineJpeg();

        // Pre-build 100 distinct JPEGs by varying the entropy-coded segment byte
        // (offset 60 in our minimal fixture — see MinimalImageFixtures). Each gets
        // a unique SHA-256 so RegisterImage can never dedupe them.
        _uniqueJpegBytes = new byte[100][];
        for (var i = 0; i < 100; i++)
        {
            var copy = (byte[])_jpegBytes.Clone();
            // Locate the entropy-coded segment placeholder and perturb it. The
            // exact offset depends on the fixture; we scan for a 0x00 between SOS
            // (0xFF 0xDA) and EOI (0xFF 0xD9).
            for (var j = 0; j < copy.Length - 1; j++)
            {
                if (copy[j] == 0xFF && copy[j + 1] == 0xDA)
                {
                    // Skip the SOS marker + 14-byte header to land in entropy-coded data.
                    var ecsOffset = j + 2 + 12; // SOS payload starts after the 12-byte header
                    if (ecsOffset < copy.Length - 2)
                    {
                        copy[ecsOffset] = (byte)(i & 0xFF);
                    }
                    break;
                }
            }
            _uniqueJpegBytes[i] = copy;
        }
    }

    [IterationSetup(Target = nameof(CacheHits_Isolated))]
    public void SetupCacheHitIteration()
    {
        // Fresh document each iteration so the cache starts in a known state with
        // exactly one entry (the first registration).
        _docWithFirstRegistration = new PdfDocument();
        _jpegStream = JpegImageXObject.Build(_jpegBytes);
        _docWithFirstRegistration.RegisterImage(_jpegStream);
    }

    [Benchmark(Description = "First image registration + Save (one-shot embed cost)")]
    public byte[] FirstRegistration_AndSave()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(JpegImageXObject.Build(_jpegBytes));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "Cache-hit-only (99 dedup hits per op, no Save)", OperationsPerInvoke = 99)]
    public int CacheHits_Isolated()
    {
        // 99 register-image calls, each must hit the warm cache. Save() is NOT in
        // the timed region — IterationSetup does the warm-up registration.
        // Returning a non-trivial int (the last ObjectNumber) forces BDN not to
        // elide the loop — PdfIndirectRef is internal so it cannot cross the
        // public benchmark-method boundary.
        var last = 0;
        for (var i = 0; i < 99; i++)
        {
            last = _docWithFirstRegistration.RegisterImage(_jpegStream).ObjectNumber;
        }
        return last;
    }

    [Benchmark(Description = "Cache-miss: register 100 unique images + Save")]
    public byte[] CacheMisses_100UniqueImages()
    {
        var doc = new PdfDocument();
        PdfIndirectRef firstRef = default!;
        for (var i = 0; i < 100; i++)
        {
            var stream = JpegImageXObject.Build(_uniqueJpegBytes[i]);
            var imageRef = doc.RegisterImage(stream);
            if (i == 0) firstRef = imageRef;
        }
        var page = doc.AddPage(MediaBoxSize.A4);
        page.PlaceImage(firstRef, 50, 50, 100, 100);
        return doc.Save();
    }
}
