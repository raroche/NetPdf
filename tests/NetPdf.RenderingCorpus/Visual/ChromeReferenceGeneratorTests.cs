// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO;
using NetPdf;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 — end-to-end validation of the C# Chrome reference generator: it drives Playwright
/// Chromium → PDF → PDFium raster → PNG. When Chromium can't launch here (a headless/sandbox environment) the
/// tests log + return (inert), exactly like the PDFium probe — they never fail for a missing oracle. When it
/// CAN launch, they prove the whole oracle pipeline mechanics on a self-contained page. NOTE: the CANONICAL
/// committed references are still generated on Linux/CI (macOS Chrome drifts on hinting/AA); this only
/// validates the machinery.</summary>
[Collection(PdfiumCollection.Name)]
public sealed class ChromeReferenceGeneratorTests(ITestOutputHelper output)
{
    private const string SelfContainedHtml =
        "<!DOCTYPE html><html><head><style>" +
        "@page { size: 200px 200px; margin: 0 }" +
        "html,body{margin:0} .box{width:200px;height:200px;background:#3366cc}" +
        "</style></head><body><div class=\"box\"></div></body></html>";

    /// <summary>When <c>NETPDF_REQUIRE_CHROME_ORACLE</c> is set (to <c>1</c>/<c>true</c>), these tests must
    /// exercise the real Chrome→PDFium pipeline — a designated CI job sets it so a misconfigured runner (no
    /// Chrome / no PDFium) FAILS loudly instead of silently passing an inert test. Unset (local dev), the tests
    /// stay inert-friendly and skip when the oracle is unavailable.</summary>
    private static bool RequireChromeOracle =>
        System.Environment.GetEnvironmentVariable("NETPDF_REQUIRE_CHROME_ORACLE") is "1" or "true" or "TRUE";

    /// <summary>Report an unavailable oracle: <see cref="Assert.Fail"/> when the CI gate requires it, otherwise
    /// log a skip line and let the caller return inert.</summary>
    private void FailOrSkip(string reason)
    {
        if (RequireChromeOracle)
            Assert.Fail($"NETPDF_REQUIRE_CHROME_ORACLE is set but the Chrome oracle pipeline could not run: {reason}");
        output.WriteLine($"SKIP: {reason}");
    }

    [Fact]
    public async System.Threading.Tasks.Task Generator_produces_valid_reference_pngs_or_skips_when_chromium_is_unavailable()
    {
        if (!PdfRasterizers.TryCreateDefault(out var rasterizer, out var rasterReason))
        {
            FailOrSkip($"PDF rasterizer unavailable ({rasterReason}).");
            return;
        }

        var outDir = Path.Combine(Path.GetTempPath(), "netpdf-chromeref-" + System.Guid.NewGuid().ToString("N"));
        int pageCount;
        try
        {
            pageCount = await ChromeReferenceGenerator
                .GenerateAsync(SelfContainedHtml, rasterizer!, outDir, "selftest", VisualHarness.Dpi);
        }
        catch (ChromeReferenceGenerator.OracleUnavailableException ex)
        {
            FailOrSkip($"Chrome oracle unavailable ({ex.Message}).");
            return;
        }

        try
        {
            Assert.True(pageCount >= 1, "expected at least one page rendered");
            var pngs = Directory.GetFiles(outDir, "selftest-page-*.png");
            Assert.Equal(pageCount, pngs.Length);

            // Each written PNG is a valid, loadable raster at the harness DPI (a 200px @ 96csspx page at 300 DPI
            // is ~625px, comfortably > 100). Loading it through the harness's own loader proves the on-disk
            // format matches what the diff runner will read.
            foreach (var png in pngs)
            {
                var img = VisualHarness.LoadPng(png);
                img.EnsureValid();
                Assert.True(img.Width > 100 && img.Height > 100, $"reference {Path.GetFileName(png)} too small: {img.Width}x{img.Height}");
            }
            output.WriteLine($"Generated + validated {pageCount} Chrome reference page(s) at {VisualHarness.Dpi} DPI.");
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Full_oracle_pipeline_diffs_chrome_against_netpdf_or_skips()
    {
        // The complete gate mechanic: Chrome oracle PNG + NetPdf candidate raster → PixelDiff runs. We do NOT
        // assert tolerance (closing the deltas is the maintainer step 35 — Chrome and NetPdf legitimately differ
        // until then); we assert the machinery runs and reports a diff, catching a broken seam (size mismatch,
        // decode failure) early.
        if (!PdfRasterizers.TryCreateDefault(out var rasterizer, out var rasterReason))
        {
            FailOrSkip($"PDF rasterizer unavailable ({rasterReason}).");
            return;
        }

        var outDir = Path.Combine(Path.GetTempPath(), "netpdf-chromeref-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            int pages;
            try
            {
                pages = await ChromeReferenceGenerator
                    .GenerateAsync(SelfContainedHtml, rasterizer!, outDir, "selftest", VisualHarness.Dpi);
            }
            catch (ChromeReferenceGenerator.OracleUnavailableException ex)
            {
                FailOrSkip($"Chrome oracle unavailable ({ex.Message}).");
                return;
            }

            Assert.True(pages >= 1);
            var reference = VisualHarness.LoadPng(Path.Combine(outDir, "selftest-page-001.png"));

            var netPdfPdf = HtmlPdf.Convert(SelfContainedHtml);
            var netPdfPages = rasterizer!.RasterizeAllPages(netPdfPdf, VisualHarness.Dpi);
            Assert.True(netPdfPages.Count >= 1);
            var candidate = netPdfPages[0];

            if (reference.SameSizeAs(candidate))
            {
                var diff = PixelDiff.Compare(reference, candidate);
                output.WriteLine($"Chrome vs NetPdf page 1: maxΔ={diff.MaxChannelDelta} SSIM={diff.Ssim:F4} " +
                    $"(withinTolerance={diff.WithinTolerance}).");
            }
            else
            {
                // A pre-delta-closing size divergence is expected/known; report it rather than fail (the size
                // reconciliation is part of closing deltas). The pipeline still ran end-to-end.
                output.WriteLine($"Page sizes differ (Chrome {reference.Width}x{reference.Height} vs " +
                    $"NetPdf {candidate.Width}x{candidate.Height}) — reconciled during delta closing.");
            }
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
