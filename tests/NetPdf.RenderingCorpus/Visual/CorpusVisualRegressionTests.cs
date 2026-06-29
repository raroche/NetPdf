// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NetPdf;
using NetPdf.RenderingCorpus.Visual;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 — the visual-regression diff runner (exit criteria 1-2). For each diffable corpus
/// invoice it renders NetPdf → PDF → raster and compares against the committed Chrome reference PNG via
/// <see cref="PixelDiff"/> (per-pixel Δ &lt; 4, SSIM ≥ 0.98).
///
/// <para>The gate is INERT until the maintainer (a) installs the PDFium backend (<see cref="PdfRasterizers"/>)
/// and (b) generates + commits the reference PNGs — see <c>tests/NetPdf.RenderingCorpus/docker/README.md</c>.
/// While either is missing the runner records WHY it skipped (via the test output — no silent pass) and
/// asserts only that NetPdf produced a non-empty PDF, so the harness stays GREEN with zero references.</para></summary>
public sealed class CorpusVisualRegressionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    public static IEnumerable<object[]> DiffableInvoices() =>
        VisualHarness.DiffableInvoices.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(DiffableInvoices))]
    public void Corpus_invoice_matches_the_chrome_reference_within_tolerance(string invoice)
    {
        var html = VisualHarness.ReadInvoiceHtml(invoice);
        var pdf = HtmlPdf.Convert(html);
        Assert.NotEmpty(pdf); // NetPdf renders something regardless of the visual backend

        var referencePath = VisualHarness.ReferencePath(invoice);
        if (!File.Exists(referencePath))
        {
            _output.WriteLine($"SKIP {invoice}: no committed reference PNG at {referencePath} "
                + "(maintainer generates references — see docker/README.md).");
            return;
        }
        if (!PdfRasterizers.TryCreateDefault(out var rasterizer, out var reason))
        {
            _output.WriteLine($"SKIP {invoice}: {reason}.");
            return;
        }

        var actual = rasterizer!.Rasterize(pdf, VisualHarness.Dpi);
        var expected = VisualHarness.LoadPng(referencePath);
        var diff = PixelDiff.Compare(expected, actual);
        _output.WriteLine($"{invoice}: {diff}");
        Assert.True(diff.WithinTolerance,
            $"{invoice} exceeds the visual tolerance: {diff}. Inspect the rendering or regenerate the reference.");
    }

    [Fact]
    public void Tailwind_cdn_invoices_are_documented_as_excluded()
    {
        // No silent caps: the two Tailwind-CDN invoices are intentionally outside the visual gate.
        Assert.Equal(2, VisualHarness.ExcludedInvoices.Count);
        foreach (var (file, reason) in VisualHarness.ExcludedInvoices)
        {
            Assert.DoesNotContain(file, VisualHarness.DiffableInvoices);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public void Every_diffable_invoice_html_exists_in_the_corpus()
    {
        foreach (var invoice in VisualHarness.DiffableInvoices)
            Assert.True(File.Exists(Path.Combine(VisualHarness.CorpusDir, invoice)),
                $"corpus invoice missing: {invoice} (looked in {VisualHarness.CorpusDir})");
    }
}
