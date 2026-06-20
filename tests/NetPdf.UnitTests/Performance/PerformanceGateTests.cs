// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Text.Fonts;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Performance;

/// <summary>
/// Phase 3 exit-criterion 7 + 8 SMOKE gates (docs/phases/phase-3-layout-and-pagination.md). These measure
/// the engine's LAYOUT → paginate → paint → PDF-write THROUGHPUT through a SYNTHETIC font resolver
/// (deterministic glyphs, no system-font I/O — platform font loading adds ~140 ms of fixed,
/// environment-dependent overhead) over TABLE fixtures (plain block-flow PROSE pagination is a tracked open
/// gap, deferrals.md <c>inline-only-block-pagination</c>). They carry ~4× headroom over the measured p50 on
/// dev hardware.
///
/// <para><b>Scope — these are guard rails, not the authoritative perf numbers.</b> The repo's standard for
/// strict perf + ALLOCATION scaling is the BenchmarkDotNet + <c>[MemoryDiagnoser]</c> flow in
/// <c>tests/NetPdf.Benchmarks/</c> on a stable runner (docs/design/performance.md). These xUnit gates run
/// everywhere (every <c>dotnet test</c>) as a coarse regression net; for that reason they are tagged
/// <c>[Trait("Category", "Performance")]</c> so CI can route them separately (review P3). Two scope caveats:
/// <list type="bullet">
/// <item>The exit-criterion-7 "20-page report" target (docs/design/performance.md) is a FULL-pipeline
/// workload — tables + images + web fonts. This gate covers only the SYNTHETIC layout/pagination pipeline;
/// the image + real-font cost belongs in a Phase-4 corpus benchmark (review P2).</item>
/// <item>Criterion 8 here is a RETAINED-heap check only (the heap stays flat across page count). It does NOT
/// enforce ALLOCATION linearity — multi-page allocation CHURN is super-linear (~O(n²),
/// deferrals.md <c>multi-page-allocation-churn</c>), which the MemoryDiagnoser standard would flag, so
/// criterion 8 is only PARTIALLY met (review P1).</item></list></para>
///
/// <para>The two p50 gates use WALL-CLOCK timing and the retained-heap gate forces full GCs, so all three
/// run in a <see cref="PerformanceGatesCollection"/> with <c>DisableParallelization = true</c> — they do
/// NOT run concurrently with the rest of the xUnit suite, which would otherwise inject CPU/GC contention
/// noise into the timing / heap measurements (Copilot review).</para>
/// </summary>
[Collection("PerformanceGates")]
public sealed class PerformanceGateTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Invoice_3_page_renders_within_200ms_p50()
    {
        var (pages, p50) = PerfFixtures.Median(PerfFixtures.Invoice(lineItems: 80), warmup: 5, iters: 15);
        Assert.Equal(3, pages);   // the fixture must actually paginate to 3 pages for the gate to be meaningful
        Assert.True(p50 <= 200.0,
            $"Exit criterion 7: the 3-page invoice p50 {p50:F1} ms exceeds the 200 ms gate.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Report_20_page_renders_within_1500ms_p50()
    {
        var (pages, p50) = PerfFixtures.Median(PerfFixtures.Report(sections: 22, rowsPerSection: 18),
            warmup: 2, iters: 7);
        // Bound BOTH sides: the deterministic synthetic fixture lands at a fixed page count, so a regression
        // that duplicates pages or over-fragments (→ 30-40 pages) must fail even if it stays under 1.5 s —
        // the workload would no longer be the intended ~22-page report (review P2).
        Assert.True(pages is >= 20 and <= 24,
            $"the report fixture should be a 20–24 page workload; got {pages} — the workload shape drifted.");
        Assert.True(p50 <= 1500.0,
            $"Exit criterion 7: the {pages}-page report p50 {p50:F1} ms exceeds the 1.5 s gate.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Multi_page_retained_memory_grows_sublinearly_with_page_count()
    {
        // Exit criterion 8 — RETAINED managed heap grows (sub-)linearly with page count. A single table at
        // ~26 rows/page renders ~5 pages vs ~20 pages (4×); the retained heap after each (the output is kept
        // alive) must not grow super-linearly. An O(n²) retention (e.g. holding every page's display list)
        // would balloon the larger render; in practice the heap stays flat because pages stream to the PDF.
        var small = RetainedHeapAfterRender(PerfFixtures.Invoice(lineItems: 130), out var smallPages);
        var big = RetainedHeapAfterRender(PerfFixtures.Invoice(lineItems: 520), out var bigPages);

        Assert.True(bigPages >= smallPages * 3,
            $"the fixtures should scale page count ~4× ({smallPages} → {bigPages} pages).");
        Assert.True(big < small * 2.0,
            $"Exit criterion 8: retained heap should grow sub-linearly with pages "
            + $"({smallPages} pg = {small / 1024} KiB → {bigPages} pg = {big / 1024} KiB; a super-linear "
            + "retention would exceed 2×).");
    }

    private static long RetainedHeapAfterRender(string html, out int pages)
    {
        var opts = new HtmlPdfOptions { FontResolver = new PerfFixtures.SynthResolver() };
        var result = HtmlPdf.ConvertDetailed(html, opts);
        pages = result.PageCount;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retained = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(result);   // keep the rendered output alive so it counts toward the retained heap
        return retained;
    }
}

/// <summary>Serializes the perf/memory gates (no parallel execution with the rest of the assembly), so
/// their wall-clock timing isn't perturbed by concurrent test classes (Copilot review).</summary>
[CollectionDefinition("PerformanceGates", DisableParallelization = true)]
public sealed class PerformanceGatesCollection { }

internal static class PerfFixtures
{
    internal sealed class SynthResolver : IFontResolver
    {
        // Build the synthetic TTF ONCE — SyntheticFont.Build() allocates the whole byte stream per call, so
        // resolving it per font query would measure the test-font builder, not the engine (Copilot review).
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }

    /// <summary>Render <paramref name="html"/> once for the page count, warm up, then return the median
    /// wall-clock over <paramref name="iters"/> renders (the p50 the exit criteria specify).
    /// <paramref name="iters"/> must be ≥ 1; an even count averages the two middle samples (Copilot review).</summary>
    internal static (int Pages, double P50Ms) Median(string html, int warmup, int iters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iters, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(warmup);
        var opts = new HtmlPdfOptions { FontResolver = new SynthResolver() };
        var pages = HtmlPdf.ConvertDetailed(html, opts).PageCount;
        for (var i = 0; i < warmup; i++) _ = HtmlPdf.Convert(html, opts);
        var samples = new double[iters];
        for (var i = 0; i < iters; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = HtmlPdf.Convert(html, opts);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        // Exact median: the middle sample for an odd count, the mean of the two middle for an even count.
        var p50 = iters % 2 == 1
            ? samples[iters / 2]
            : (samples[iters / 2 - 1] + samples[iters / 2]) / 2.0;
        return (pages, p50);
    }

    /// <summary>A paginating invoice: a header + an N-row line-item table (tables fragment across pages).
    /// ~26 rows/page with the synthetic font at 13 px, so 80 rows ≈ 3 pages.</summary>
    internal static string Invoice(int lineItems)
    {
        var sb = new StringBuilder("<!DOCTYPE html><html><head><style>"
            + "body { font-size: 13px } h1 { font-size: 24px } table { width: 100% }"
            + "td { padding: 4px; border-bottom: 1px solid #ccc }"
            + "@page { @bottom-center { content: counter(page) } }"
            + "</style></head><body><h1>Invoice 0042</h1>"
            + "<p>Bill to: Acme Corp &middot; 123 Market St &middot; Date 2026-06-19</p><table>");
        for (var i = 0; i < lineItems; i++)
            sb.Append("<tr><td>Item ").Append(i).Append("</td><td>Description of line item number ")
              .Append(i).Append("</td><td>").Append((i % 9) + 1).Append("</td><td>$")
              .Append((i * 7) % 500).Append(".00</td></tr>");
        return sb.Append("</table><p>Thank you for your business.</p></body></html>").ToString();
    }

    /// <summary>A paginating multi-section tabular report (each section a heading + a small table; the
    /// tables fragment across pages). 22 sections × 18 rows ≈ 22 pages.</summary>
    internal static string Report(int sections, int rowsPerSection)
    {
        var sb = new StringBuilder("<!DOCTYPE html><html><head><style>"
            + "body { font-size: 13px } h2 { font-size: 18px } table { width: 100% }"
            + "td { padding: 3px } @page { @bottom-center { content: counter(page) } }"
            + "</style></head><body>");
        for (var s = 0; s < sections; s++)
        {
            sb.Append("<h2>Section ").Append(s).Append("</h2><table>");
            for (var r = 0; r < rowsPerSection; r++)
                sb.Append("<tr><td>Row ").Append(r).Append(" of section ").Append(s)
                  .Append("</td><td>Some tabular content value ").Append(r * 3)
                  .Append("</td><td>").Append((r * 11) % 1000).Append("</td></tr>");
            sb.Append("</table>");
        }
        return sb.Append("</body></html>").ToString();
    }
}
