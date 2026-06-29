# Visual-regression harness — reference generation runbook (maintainer)

The visual-regression gate (`Visual/CorpusVisualRegressionTests`) diffs NetPdf's render of each diffable
corpus invoice against a **committed Chrome reference PNG**. This directory holds the maintainer tooling to
generate those references reproducibly. The diff core (`PixelDiff`), the runner, and the rasterizer seam
(`IPdfRasterizer`) already ship and are unit-tested; the gate stays **inert (green, skipping)** until both of
the maintainer steps below are done.

## What the agent already landed (managed core)

| Piece | Where |
|---|---|
| `PixelDiff` — per-pixel RGBA Δ + mean SSIM, tolerance `Δ < 4`, `SSIM ≥ 0.98` | `../Visual/PixelDiff.cs` (+ tests) |
| `IPdfRasterizer` seam + `PdfRasterizers.TryCreateDefault` (reports unavailable today) | `../Visual/IPdfRasterizer.cs` (+ tests) |
| `CorpusVisualRegressionTests` — diffs, or SKIPS with a logged reason when refs / PDFium absent | `../Visual/CorpusVisualRegressionTests.cs` |
| `VisualHarness` — paths, `Dpi = 300`, diffable vs excluded invoice lists, PNG loader | `../Visual/VisualHarness.cs` |
| Reference PNGs land here | `../references/` |

## Step 1 — install PDFium (NetPdf-side PDF → raster)

SkiaSharp can **write** PDF but cannot **read** it, so the NetPdf-side rasterization needs PDFium.

1. Add a PDFium package to `NetPdf.RenderingCorpus.csproj` (e.g. `PDFiumCore` or `bblanchon.PDFium`), or use
   `pdftoppm` (poppler) / Ghostscript as a CLI fallback.
2. Implement `IPdfRasterizer` against it (render page 1 at `VisualHarness.Dpi` → RGBA `RasterImage`) and
   return it from `PdfRasterizers.TryCreateDefault` instead of the current `false`.
3. The runner stops skipping on the "no PDF rasterizer configured" reason and starts rasterizing NetPdf output.

## Step 2 — generate + commit references (pinned Chrome, Linux/Docker)

Generate references from a **pinned Chrome on Linux** so CI (Linux) does not drift against macOS fonts /
anti-aliasing. A **pinned font pack** used by BOTH Chrome and NetPdf (wire a `FontResolver` on the NetPdf
side) is essential — otherwise system Helvetica/Segoe substitution makes the diff noisy.

```bash
# from the repo root
docker build -t netpdf-visual-refs tests/NetPdf.RenderingCorpus/docker
docker run --rm -v "$PWD:/work" -w /work netpdf-visual-refs \
    python3 tests/NetPdf.RenderingCorpus/docker/generate-references.py
git add tests/NetPdf.RenderingCorpus/references/*.png   # commit the regenerated references
```

`generate-references.py` drives Playwright Chromium: for each diffable invoice it prints the page to PDF and
rasterizes it at `VisualHarness.Dpi` (300) via `pypdfium2`, writing `references/<stem>.png`. Pin the
Chromium revision via the Playwright version in the Dockerfile (never `latest`).

> **Reference regeneration is a deliberate manual step — never run automatically in CI.** Upstream
> Chrome / font drift must never silently change what the tests assert.

## Step 3 — wire CI

Add a GitHub Actions job that runs the Docker image and executes the visual gate on Linux for every PR
(`dotnet test tests/NetPdf.RenderingCorpus`). Keep reference regeneration in a SEPARATE manual workflow.

## Step 4 — close deltas, then release

Run the now-live gate; above-tolerance mismatches are real engine bugs (gradient stepping, shadow blur σ,
font metrics, default margins, sub-pixel placement) — fix them, don't chase sub-threshold pixels
(`Δ < 4` is the contract). Then add the CHANGELOG `0.9.0-rc1` entry + version bump and apply the annotated
`0.9.0-rc1` tag (internal — the public flip + NuGet happen at `1.0.0` in Phase 5).

## Excluded invoices (no silent caps)

`02-tailwind-cdn.html` and `03-tailwind-cdn-responsive.html` are **excluded** from the gate: they require
runtime JS to emit their Tailwind utility CSS, so neither Chrome-without-JS nor NetPdf renders them as
authored. Pre-compile Tailwind to static CSS to bring them in. The exclusion is encoded in
`VisualHarness.ExcludedInvoices` and asserted by a test.
