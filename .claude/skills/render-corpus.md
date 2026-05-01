---
name: render-corpus
description: Render every corpus HTML file end-to-end through NetPdf and run the visual-regression diff against committed Chromium references. Phase 3+.
---

# Skill: render-corpus

Drive the full corpus through NetPdf's pipeline and report visual-regression results. The single most useful "is the engine working?" check during Phase 3 and beyond.

## Prerequisites

- Phase 3 has begun (`HtmlPdf.Convert` no longer throws `NotImplementedException`).
- Corpus files exist under `tests/NetPdf.RealDocuments/Corpus/`.
- (For visual diff): committed reference PNGs exist under `tests/NetPdf.RenderingCorpus/References/`. If they don't, this skill runs in **smoke mode** — it just renders and validates structure, no pixel diff.

## Steps

1. **Build the project** if needed:
   ```
   dotnet build NetPdf.slnx -c Release
   ```

2. **Locate every corpus HTML file** under `tests/NetPdf.RealDocuments/Corpus/**/*.html`. Sort by category and number.

3. **For each file**:
   a. Render via the sample CLI:
      ```
      dotnet run --project samples/invoice-cli/InvoiceCli.csproj -c Release -- \
        <corpus-file>.html /tmp/netpdf-corpus/<corpus-file>.pdf
      ```
   b. Validate structure with `qpdf` (if installed):
      ```
      qpdf --check /tmp/netpdf-corpus/<corpus-file>.pdf
      ```
      Pass = no warnings.
   c. Validate structure with PDFium (if integrated in `tests/NetPdf.PdfValidation`): assert page count > 0, all text extractable.
   d. (If reference PNG exists): rasterize the PDF to PNG and run pixel-diff:
      ```
      dotnet test tests/NetPdf.RenderingCorpus/NetPdf.RenderingCorpus.csproj -c Release \
        --filter "FullyQualifiedName~<corpus-file>"
      ```
      Tolerance: per-pixel RGBA delta < 4, SSIM > 0.98.

4. **Aggregate results** into a table:
   ```
   File                                   Render  qpdf  Structure  Pixel-diff  Status
   Invoices/01-classic-pure-css.html      ✅      ✅    ✅          ΔRGBA<4 ✅  PASS
   Invoices/02-tailwind-cdn.html          ✅      ✅    ✅          DEGRADED*   EXPECTED
   Invoices/03-tailwind-cdn-responsive    ✅      ✅    ✅          DEGRADED*   EXPECTED
   Invoices/04-anvil-running-elements     ✅      ✅    ✅          ΔRGBA<4 ✅  PASS
                                                                                
   * Tailwind via CDN documented as not-supported without JS.
   ```

5. **Save diff images** (if any failures) to `TestResults/diffs/`. Print the paths for one-click review.

6. **Print a summary**: total files, pass count, expected-degraded count, fail count.

## Output

A clear pass/fail status per file, with paths to diff images for any failures. No code changes.

## Failure modes & responses

- **NetPdf throws** during render → Phase 3 has a bug. Capture the exception and stack; this is the next priority to fix.
- **qpdf reports warnings** → PDF byte format issue. Run the `aot-check` skill first to rule out AOT regressions, then investigate `NetPdf.Pdf` content stream emission.
- **Pixel diff exceeds tolerance** → real visual regression. Compare diff PNG to reference PNG by eye; identify which CSS feature differs; file an issue or fix.
- **Pixel diff exceeds tolerance on a known-degraded file (Tailwind CDN samples)** → expected. Don't regenerate the reference; the documentation calls this out as a limitation.

## Style notes

- This skill is read-only against the codebase. It produces test results, not code changes.
- Run as part of every PR that touches `NetPdf.Layout`, `NetPdf.Paint`, or `NetPdf.Pdf`.
