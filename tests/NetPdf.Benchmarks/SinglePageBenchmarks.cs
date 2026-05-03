// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using BenchmarkDotNet.Attributes;
using NetPdf.Pdf;

namespace NetPdf.Benchmarks;

/// <summary>
/// Fixed-shape (non-parameterized) baselines for the absolute simplest documents.
/// These numbers are the floor of "how fast can the byte writer go" — every other
/// benchmark builds on top of this work.
/// <para>
/// <see cref="BlankSinglePage"/> is marked as the BDN baseline so the report
/// includes a Ratio column comparing the other benchmarks to it.
/// </para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class SinglePageBenchmarks
{
    [Benchmark(Description = "Single blank A4 page -> bytes (writer floor)", Baseline = true)]
    public byte[] BlankSinglePage()
    {
        var doc = new PdfDocument();
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }

    [Benchmark(Description = "Single page + simple content stream -> bytes")]
    public byte[] SinglePageWithContent()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(MediaBoxSize.A4);
        page.AppendContent("0.5 0.5 0.5 rg 50 50 100 100 re f\n");
        page.AppendContent("0 0 0 rg 200 200 50 50 re S\n");
        return doc.Save();
    }

    [Benchmark(Description = "Single page + Info dict (Title/Author/Subject/Keywords/Creator)")]
    public byte[] SinglePageWithFullMetadata()
    {
        var doc = new PdfDocument
        {
            Title = "Benchmark",
            Author = "NetPdf.Benchmarks",
            Subject = "Phase 1",
            Keywords = "pdf, benchmark, baseline",
            Creator = "NetPdf",
            CreationDate = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
        };
        doc.AddPage(MediaBoxSize.A4);
        return doc.Save();
    }
}
