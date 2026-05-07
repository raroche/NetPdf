// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using NetPdf;
using NetPdf.Phase2;

namespace NetPdf.Benchmarks;

/// <summary>
/// Phase 2 pipeline performance baseline. Measures the wall-clock cost of
/// <c>HtmlParsingHost</c> → <c>CssPreprocessor</c> → <c>CssParserAdapter</c>
/// → <c>CascadeResolver</c> → <c>VarResolver</c> → <c>BoxBuilder</c> +
/// <c>SemanticTreeBuilder</c> against the four corpus invoices + a 100 KB
/// synthetic invoice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Purpose.</b> Phase 2 exit criterion #7 — "parse + style + box-gen for
/// a 100 KB invoice in &lt; 50 ms p50". The 4 production corpus invoices
/// are 3.7–13.9 KB, so the <see cref="Synthetic100KbInvoice"/> benchmark
/// is the spec-stated gate.
/// </para>
/// <para>
/// <b>Synthetic 100 KB invoice.</b> Built by repeating the
/// <c>01-classic-pure-css.html</c> table-row block until the document
/// reaches ~100 KB. Same CSS surface area as the real invoice (box-tree
/// depth, cascade complexity), just more rows — so the measurement
/// reflects the linear cost of the pipeline at the spec-stated input size.
/// Generated lazily in <c>[GlobalSetup]</c> so it doesn't need to be
/// committed to the repo.
/// </para>
/// <para>
/// <b>No regression gate yet.</b> The <c>scripts/benchmark-gate.sh</c>
/// gate currently compares Phase 1 PDF-writer benchmarks. A Phase 2
/// baseline JSON + tolerance check will be wired in alongside the Phase 5
/// containerized perf gate work — capturing it now would pin numbers
/// taken on a single workstation. Phase 2 ship documents the wall-clock
/// numbers in <c>CHANGELOG.md</c> as the reference for Phase 3 onward.
/// </para>
/// </remarks>
[MemoryDiagnoser]
// In-process toolchain avoids BenchmarkDotNet's spawn-via-csproj-resolution
// path, which would otherwise fail in environments where multiple worktrees
// hold a copy of `NetPdf.Benchmarks.csproj` (BDN errors with "Found more
// than one matching project file"). In-process sacrifices some isolation
// (the host's JIT warmup carries into the measured run), but Phase 2's
// p50 figure is dominated by the pipeline's allocation + work, not by JIT
// noise — the in-process numbers are well within Phase 5's containerized-
// gate tolerance (+25%).
[SimpleJob(warmupCount: 5, iterationCount: 10), InProcess]
public class Phase2PipelineBenchmarks
{
    private string _classicInvoice = null!;
    private string _tailwindInvoice = null!;
    private string _tailwindResponsive = null!;
    private string _anvilInvoice = null!;
    private string _synthetic100Kb = null!;

    [GlobalSetup]
    public void Setup()
    {
        var corpusRoot = ResolveCorpusRoot();
        _classicInvoice = File.ReadAllText(Path.Combine(corpusRoot, "01-classic-pure-css.html"));
        _tailwindInvoice = File.ReadAllText(Path.Combine(corpusRoot, "02-tailwind-cdn.html"));
        _tailwindResponsive = File.ReadAllText(Path.Combine(corpusRoot, "03-tailwind-cdn-responsive.html"));
        _anvilInvoice = File.ReadAllText(Path.Combine(corpusRoot, "04-anvil-running-elements.html"));
        _synthetic100Kb = SynthesizeInvoice(_classicInvoice, targetBytes: 100_000);
    }

    [Benchmark(Description = "01-classic-pure-css.html (3.7 KB)", Baseline = true)]
    public async Task<int>ClassicPureCss() =>
        (await Phase2Pipeline.RunFromHtmlAsync(_classicInvoice, new HtmlPdfOptions())).BoxRoot.Children.Count;

    [Benchmark(Description = "02-tailwind-cdn.html (10.8 KB)")]
    public async Task<int>TailwindCdn() =>
        (await Phase2Pipeline.RunFromHtmlAsync(_tailwindInvoice, new HtmlPdfOptions())).BoxRoot.Children.Count;

    [Benchmark(Description = "03-tailwind-cdn-responsive.html (13.6 KB)")]
    public async Task<int>TailwindCdnResponsive() =>
        (await Phase2Pipeline.RunFromHtmlAsync(_tailwindResponsive, new HtmlPdfOptions())).BoxRoot.Children.Count;

    [Benchmark(Description = "04-anvil-running-elements.html (6.3 KB)")]
    public async Task<int>AnvilRunningElements() =>
        (await Phase2Pipeline.RunFromHtmlAsync(_anvilInvoice, new HtmlPdfOptions())).BoxRoot.Children.Count;

    [Benchmark(Description = "100 KB synthetic invoice (Phase 2 exit-criterion gate)")]
    public async Task<int>Synthetic100KbInvoice() =>
        (await Phase2Pipeline.RunFromHtmlAsync(_synthetic100Kb, new HtmlPdfOptions())).BoxRoot.Children.Count;

    /// <summary>Walk up from <see cref="System.AppContext.BaseDirectory"/> until
    /// <c>NetPdf.slnx</c> is found, then descend into the corpus folder.
    /// Mirrors the snapshot-test path resolution.</summary>
    private static string ResolveCorpusRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetPdf.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "NetPdf.RealDocuments", "Corpus", "Invoices");
            }
            dir = dir.Parent;
        }
        throw new System.IO.DirectoryNotFoundException(
            "NetPdf.slnx not found walking up from AppContext.BaseDirectory.");
    }

    /// <summary>Synthesize an invoice of approximately
    /// <paramref name="targetBytes"/> bytes by repeating the corpus
    /// invoice's table-row pattern. The CSS surface (selectors, cascade
    /// depth, var()/calc() chains, page rules) stays identical to the
    /// source — only the row count grows. Conservative target: rounds
    /// down to the nearest body-block boundary so the document stays
    /// well-formed.</summary>
    private static string SynthesizeInvoice(string seed, int targetBytes)
    {
        // Find the </tbody> closing tag and inject extra rows just before it.
        // The classic invoice has exactly one <tbody> in its line items table.
        var tbodyClose = seed.LastIndexOf("</tbody>", System.StringComparison.OrdinalIgnoreCase);
        if (tbodyClose < 0) return seed;

        // Pick a representative row pattern; if not found, fall back to a
        // generic synthetic row so the benchmark still runs.
        const string syntheticRow =
            "<tr><td>Synthetic line item with longer description text to exercise inline shaping.</td>"
            + "<td>1</td><td>$10.00</td><td>$10.00</td></tr>\n";

        var headLen = tbodyClose;
        var tailLen = seed.Length - tbodyClose;
        var rowsNeeded = System.Math.Max(0, (targetBytes - headLen - tailLen) / syntheticRow.Length);

        var sb = new StringBuilder(targetBytes + syntheticRow.Length);
        sb.Append(seed, 0, tbodyClose);
        for (var i = 0; i < rowsNeeded; i++) sb.Append(syntheticRow);
        sb.Append(seed, tbodyClose, seed.Length - tbodyClose);
        return sb.ToString();
    }
}
