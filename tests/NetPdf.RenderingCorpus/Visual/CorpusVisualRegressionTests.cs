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
/// invoice it renders NetPdf → PDF → per-page rasters and compares each page against the committed Chrome
/// reference PNGs via <see cref="PixelDiff"/> (per-pixel Δ &lt; 4, SSIM ≥ 0.98).
///
/// <para>Activation follows <see cref="VisualGatePolicy"/>: while NO reference is committed (and the gate
/// isn't forced via <see cref="VisualGatePolicy.RequiredEnvVar"/>) the runner stays inert (logged skip). Once
/// a reference exists — or the gate is required — a missing / unconfigured PDFium backend is a hard FAILURE,
/// so CI can't go green by silently never rasterizing (PR-242 review [P1]). Diffable invoices live in the
/// harness's own self-contained corpus and carry no remote resources (guarded below).</para></summary>
[Collection(PdfiumCollection.Name)]
public sealed class CorpusVisualRegressionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    public static IEnumerable<object[]> DiffableInvoices() =>
        VisualHarness.DiffableInvoices.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(DiffableInvoices))]
    public void Corpus_invoice_converts_to_a_pdf(string invoice)
    {
        // End-to-end smoke independent of the visual backend: NetPdf renders the (self-contained) invoice.
        var pdf = HtmlPdf.Convert(VisualHarness.ReadInvoiceHtml(invoice));
        Assert.NotEmpty(pdf);
    }

    [Theory]
    [MemberData(nameof(DiffableInvoices))]
    public void Corpus_invoice_matches_the_chrome_reference_within_tolerance(string invoice)
    {
        var referenceExists = VisualHarness.ReferenceExists(invoice);
        var rasterizerAvailable = PdfRasterizers.TryCreateDefault(out var rasterizer, out var unavailableReason);
        var decision = VisualGatePolicy.Decide(referenceExists, rasterizerAvailable, VisualGatePolicy.IsRequiredByEnv());

        switch (decision.Action)
        {
            case VisualGateAction.Skip:
                _output.WriteLine($"SKIP {invoice}: {decision.Reason}.");
                return;
            case VisualGateAction.Fail:
                Assert.Fail($"{invoice}: {decision.Reason}"
                    + (rasterizerAvailable ? "" : $" [{unavailableReason}]"));
                return;
        }

        // Diff: rasterize every NetPdf page and diff page-for-page against the committed references.
        var pdf = HtmlPdf.Convert(VisualHarness.ReadInvoiceHtml(invoice));
        var pages = rasterizer!.RasterizeAllPages(pdf, VisualHarness.Dpi);
        var references = VisualHarness.ReferencePagePaths(invoice);
        Assert.True(pages.Count == references.Count,
            $"{invoice}: NetPdf produced {pages.Count} page(s) but {references.Count} reference(s) are committed");

        for (var i = 0; i < pages.Count; i++)
        {
            var expected = VisualHarness.LoadPng(references[i]);
            var diff = PixelDiff.Compare(expected, pages[i]);
            _output.WriteLine($"{invoice} page {i + 1}: {diff}");
            Assert.True(diff.WithinTolerance,
                $"{invoice} page {i + 1} exceeds the visual tolerance: {diff}. "
                + "Inspect the rendering or regenerate the reference.");
        }
    }

    [Fact]
    public void Diffable_invoices_have_no_remote_resources()
    {
        // PR-242 review [P1] — a diffable invoice must be self-contained (no fetched http(s) resource), else
        // Chrome would fetch what NetPdf's SafeDefault blocks and the gate would be nondeterministic.
        foreach (var invoice in VisualHarness.DiffableInvoices)
        {
            var remote = VisualHarness.RemoteResourceUrls(VisualHarness.ReadInvoiceHtml(invoice));
            Assert.True(remote.Count == 0,
                $"{invoice} references remote resources (vendor them as data: URIs first): {string.Join(", ", remote)}");
        }
    }

    [Fact]
    public void Excluded_invoices_are_documented_and_not_diffable()
    {
        // No silent caps: every upstream invoice kept out of the gate has a recorded reason.
        Assert.NotEmpty(VisualHarness.ExcludedInvoices);
        foreach (var (file, reason) in VisualHarness.ExcludedInvoices)
        {
            Assert.DoesNotContain(file, VisualHarness.DiffableInvoices);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }

    [Fact]
    public void Every_diffable_invoice_html_exists_in_the_harness_corpus()
    {
        Assert.NotEmpty(VisualHarness.DiffableInvoices);
        foreach (var invoice in VisualHarness.DiffableInvoices)
            Assert.True(File.Exists(Path.Combine(VisualHarness.CorpusDir, invoice)),
                $"harness corpus invoice missing: {invoice} (looked in {VisualHarness.CorpusDir})");
    }
}
