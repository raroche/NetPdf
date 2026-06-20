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
/// Phase 3 exit-criterion 7 + 8 enforced gates (docs/phases/phase-3-layout-and-pagination.md). The perf
/// gates render through a SYNTHETIC font resolver (deterministic glyphs, no system-font I/O) so they
/// measure the layout → paginate → paint → PDF-write pipeline — the engine's own throughput — not platform
/// font loading (which adds ~140 ms of fixed, environment-dependent overhead). Fixtures use TABLE content,
/// which fragments across pages on the live multi-page path; plain block-flow PROSE pagination is a tracked
/// open gap (deferrals.md <c>inline-only-block-pagination</c>). Thresholds carry ~4× headroom over the
/// measured p50 on dev hardware.
/// </summary>
public sealed class PerformanceGateTests
{
    [Fact]
    public void Invoice_3_page_renders_within_200ms_p50()
    {
        var (pages, p50) = PerfFixtures.Median(PerfFixtures.Invoice(lineItems: 80), warmup: 5, iters: 15);
        Assert.Equal(3, pages);   // the fixture must actually paginate to 3 pages for the gate to be meaningful
        Assert.True(p50 <= 200.0,
            $"Exit criterion 7: the 3-page invoice p50 {p50:F1} ms exceeds the 200 ms gate.");
    }
}

internal static class PerfFixtures
{
    internal sealed class SynthResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }

    /// <summary>Render <paramref name="html"/> once for the page count, warm up, then return the median
    /// wall-clock over <paramref name="iters"/> renders (the p50 the exit criteria specify).</summary>
    internal static (int Pages, double P50Ms) Median(string html, int warmup, int iters)
    {
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
        return (pages, samples[iters / 2]);
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
}
