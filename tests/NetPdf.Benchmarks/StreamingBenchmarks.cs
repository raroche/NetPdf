// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.TestKit;

namespace NetPdf.Benchmarks;

/// <summary>
/// Streaming-output benchmarks. <see cref="PdfDocument.Save"/> returns a fresh
/// <c>byte[]</c>; <see cref="PdfDocument.SaveTo(IBufferWriter{byte})"/> writes
/// directly into a caller-provided buffer with no final array copy. Real consumers
/// (e.g., HTTP response bodies, file streams) should use <c>SaveTo</c>; this suite
/// confirms it allocates roughly half the bytes (no <c>byte[]</c> result allocation
/// + no copy step) at comparable wall time.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class StreamingBenchmarks
{
    private byte[] _jpegBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _jpegBytes = MinimalImageFixtures.MinimalBaselineJpeg();
    }

    [Benchmark(Description = "Save() -> byte[] (allocates final array + copies)")]
    public byte[] Save_AllocatesArray()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(JpegImageXObject.Build(_jpegBytes));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        return doc.Save();
    }

    [Benchmark(Description = "SaveTo(ArrayBufferWriter) -> no final byte[] copy")]
    public int SaveTo_StreamingBuffer()
    {
        var doc = new PdfDocument();
        var imageRef = doc.RegisterImage(JpegImageXObject.Build(_jpegBytes));
        doc.AddPage(MediaBoxSize.A4).PlaceImage(imageRef, 50, 50, 100, 100);
        var buffer = new ArrayBufferWriter<byte>(initialCapacity: 4096);
        doc.SaveTo(buffer);
        return buffer.WrittenCount;
    }
}
