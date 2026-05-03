// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;

namespace NetPdf.Benchmarks;

/// <summary>
/// Page-count scaling benchmarks. Verify that <see cref="PdfDocument.Save"/>'s wall
/// clock and allocation profile both grow <b>linearly</b> in <see cref="PageCount"/>
/// across a 4-order-of-magnitude span (1, 10, 100, 1000). Anything that introduces
/// O(N²) per-page work — a duplicate dictionary scan, a quadratic xref re-allocation,
/// an inadvertent SHA per page — would surface here as a per-page allocation that
/// climbs with N.
/// <para>
/// Higher iteration counts (warmup=5, iter=10) than the rest of the suite because
/// the 1000-page run amortizes JIT warmup across many ops, but the 1-page run is
/// noisy under default settings (~28% margin on the prior baseline).
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class PageScalingBenchmarks
{
    [Params(1, 10, 100, 1000)]
    public int PageCount { get; set; }

    [Benchmark(Description = "N blank pages -> bytes")]
    public byte[] BlankPages()
    {
        var doc = new PdfDocument();
        for (var i = 0; i < PageCount; i++)
        {
            doc.AddPage(MediaBoxSize.A4);
        }
        return doc.Save();
    }

    [Benchmark(Description = "N pages with simple content stream -> bytes")]
    public byte[] PagesWithSimpleContentStream()
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
}
