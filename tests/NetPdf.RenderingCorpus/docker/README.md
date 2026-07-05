# Visual-regression harness — reference generation runbook (maintainer)

The visual-regression gate (`Visual/CorpusVisualRegressionTests`) diffs NetPdf's render of each diffable
corpus invoice against a **committed Chrome reference PNG**. This directory holds the maintainer tooling to
generate those references reproducibly. The diff core (`PixelDiff`), the runner, and the rasterizer seam
(`IPdfRasterizer`) already ship and are unit-tested; the gate stays **inert (green, skipping)** until the
reference-generation step below is done (Step 1 / PDFium is already complete, and `0.9.0-rc1` is already tagged).

## What the agent already landed (managed core)

| Piece | Where |
|---|---|
| `PixelDiff` — per-pixel RGBA Δ + mean SSIM, tolerance `Δ < 4`, `SSIM ≥ 0.98` | `../Visual/PixelDiff.cs` (+ tests) |
| `IPdfRasterizer` seam + `PdfRasterizers.TryCreateDefault` (**PDFium backend WIRED** via `PDFtoImage`) | `../Visual/IPdfRasterizer.cs`, `../Visual/PdfiumRasterizer.cs` (+ tests) |
| `VisualGatePolicy` — inert until a reference exists / the gate is forced; then a missing rasterizer FAILS | `../Visual/VisualGatePolicy.cs` (+ tests) |
| `CorpusVisualRegressionTests` — per-page diff, or skip/FAIL per the policy | `../Visual/CorpusVisualRegressionTests.cs` |
| `VisualHarness` — paths, `Dpi = 300`, self-contained corpus, diffable/excluded lists, remote-resource guard, PNG loader | `../Visual/VisualHarness.cs` (+ tests) |
| Self-contained corpus (remote assets vendored as data: URIs) | `../corpus/` |
| Reference PNGs land here, named `<stem>-page-NNN.png` (per page) | `../references/` |

## Activation policy (PR-242 review [P1])

The gate is **inert** only while there are zero committed references AND it isn't forced. Once a reference
PNG exists for an invoice — OR `NETPDF_VISUAL_REGRESSION_REQUIRED=1` is set — a missing / unwired PDFium
backend (or a missing per-invoice reference under the forced flag) is a **hard FAIL**. So CI cannot go green
after references are committed if the native backend was never installed.

## Self-contained corpus (PR-242 review [P1])

Diffable invoices live in `../corpus/` and MUST be **self-contained** — no remote `http(s)` resources (a
guard test enforces this). A remote asset is blocked by NetPdf's `SafeDefault` yet fetched by Chrome, which
makes the diff nondeterministic; vendor such assets as inline `data:` URIs. Two invoices are now vendored +
diffable: `01-classic-pure-css.html` and `04-anvil-running-elements.html` (its two upstream remote raster
logos were replaced by inline SVG `data:` URIs). **Generate + commit references for BOTH** — the full
per-page set for `01-classic` and `04-anvil`. Only the Tailwind-CDN invoices (`02`, `03`) stay excluded
(they need runtime JS to emit their utility CSS).

## Step 1 — PDFium (NetPdf-side PDF → raster) — ✅ DONE (no install)

SkiaSharp can **write** PDF but cannot **read** it, so the NetPdf-side rasterization uses PDFium. This is now
**wired in-process** via the `PDFtoImage` test dependency (which ships `bblanchon.PDFium` native assets for
macOS / Linux / Windows — no separate install):

- `PdfiumRasterizer` (`../Visual/PdfiumRasterizer.cs`) renders EVERY page at `VisualHarness.Dpi` (300) → one
  RGBA `RasterImage` per page (read via `SKBitmap.Pixels`, native-order-independent).
- `PdfRasterizers.TryCreateDefault` runs a cheap native-load probe (renders a trivial NetPdf PDF); it returns
  the `PdfiumRasterizer` on success, or reports unavailable (the runner skips) if the native lib can't load.
- The `PDFtoImage` SkiaSharp floor (`3.119.2`) is the **repo-wide** SkiaSharp pin, so the harness measures the
  SAME renderer dependency set as production (no per-project version split). The byte-identity gates were
  re-verified after the `3.119.0 → 3.119.2` patch bump.

So the ONLY remaining maintainer step is generating + committing the Chrome reference PNGs (Step 2); the
NetPdf-side rasterization is live and unit-tested (`PdfRasterizerTests`).

## Step 2 — generate + commit references (pinned Chrome, Linux/Docker)

> **This is the ONE thing standing between here and a fully-green Phase 4** (`0.9.0-rc1` is already tagged;
> exit criteria 3–10 are met). Generating + committing the canonical Chrome reference PNGs is **genuinely not
> producible from a macOS dev environment**: Linux CI drifts against macOS font hinting / anti-aliasing (→ false
> diffs), and a dev box may have no container runtime at all to run the pinned-Linux image. **Do not commit
> macOS-generated references** — they would poison the gate. This step must run on **Linux/CI**; once the PNGs
> land under `../references/`, `VisualGatePolicy` auto-activates the enforcing diff gate and Phase-4 exit
> criteria 1–2 flip green. It is carried into early Phase 5's cross-platform-CI deliverable.

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

`generate-references.py` drives Playwright Chromium: for each diffable invoice it prints to PDF and
rasterizes EVERY page at `VisualHarness.Dpi` (300) via `pypdfium2`, writing one PNG per page —
`references/<stem>-page-NNN.png` (1-based), matching the runner's per-page diff contract. Pin the Chromium
revision via the Playwright version in the Dockerfile (never `latest`).

> **Reference regeneration is a deliberate manual step — never run automatically in CI.** Upstream
> Chrome / font drift must never silently change what the tests assert.

## Step 3 — wire CI

Add a GitHub Actions job that runs the Docker image and executes the visual gate on Linux for every PR
(`dotnet test tests/NetPdf.RenderingCorpus`). Keep reference regeneration in a SEPARATE manual workflow.

## Step 4 — close deltas

Run the now-live gate; above-tolerance mismatches are real engine bugs (gradient stepping, shadow blur σ,
font metrics, default margins, sub-pixel placement) — fix them, don't chase sub-threshold pixels
(`Δ < 4` is the contract).

> **Release plumbing is already done.** The CHANGELOG `0.9.0-rc1` entry, the version bump, and the annotated
> `0.9.0-rc1` tag (→ `73493ad`) all landed at the Phase-4 closeout — do **not** redo them. The public flip +
> NuGet publication happen at `1.0.0` in Phase 5.

## Excluded invoices (no silent caps)

`02-tailwind-cdn.html` and `03-tailwind-cdn-responsive.html` are **excluded** from the gate: they require
runtime JS to emit their Tailwind utility CSS, so neither Chrome-without-JS nor NetPdf renders them as
authored. Pre-compile Tailwind to static CSS to bring them in. The exclusion is encoded in
`VisualHarness.ExcludedInvoices` and asserted by a test.
