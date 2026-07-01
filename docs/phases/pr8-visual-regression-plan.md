# PR 8 — Visual-regression harness + `0.9.0-rc1` — session plan

**Status:** 🟢 MACHINERY SHIPPED — the harness landed across #254 (PDFium rasterizer), #255 (C# Chrome oracle), and #258 (`0.9.0-rc1` staged). Criteria **9 met** (Docker reproducible + documented regen) and **10 met** (`0.9.0-rc1` tagged at closeout). **The one remaining item is a maintainer/CI action:** generate the canonical per-page Chrome reference PNGs on **Linux** and commit them under `tests/NetPdf.RenderingCorpus/references/` — that flips criteria **1–2** green and `VisualGatePolicy` auto-activates the enforcing gate. (macOS-generated references drift on font hinting/AA → false diffs, so this must run on Linux/CI, not a dev machine.)
**Closes Phase-4 exit criteria:** 1 (visual diffs within tolerance) + 2 (all corpus invoices match Chrome) — **pending the Linux reference commit**; 9 + 10 already met.
**Phase-4 doc tasks:** 30–36 in [phase-4-visual-parity.md](phase-4-visual-parity.md).
**Handoff:** the enforcing visual gate goes live (and visual fidelity "locks") the moment the Linux references land — carried into early Phase 5 (packaging/release, [phase-5-packaging-and-release.md](phase-5-packaging-and-release.md)).

---

## 1. What it does (architecture)

Render each corpus doc two ways and pixel-diff them:

```
corpus HTML ──┬─► Chrome (Playwright, pinned/Docker) ─► print-to-PDF ─► rasterize@300dpi ─► reference.png  (COMMITTED)
              └─► NetPdf.HtmlPdf.Convert            ─► our PDF       ─► rasterize@300dpi ─► actual.png
                                                                            │
                                          SSIM + per-pixel diff ◄───────────┘
                                          tolerance: per-pixel RGBA Δ < 4, SSIM > 0.98
```

- **CI runs the diff on every PR** against the committed references.
- **Reference regeneration is a SEPARATE manual workflow** so upstream Chrome/font drift never silently changes tests.
- Home: **`tests/NetPdf.RenderingCorpus/`** (already references `Microsoft.Playwright`; currently only `PhaseZeroSmoke`).
- Corpus HTML: **`tests/NetPdf.RealDocuments/Corpus/Invoices/`**.

---

## 2. The sandbox-vs-maintainer split (READ FIRST)

The agent CANNOT run the full harness in its sandbox (no Docker, no Chrome, no PDFium, and locally-generated references wouldn't be reproducible/pinned). So the work splits:

| The agent CAN land in-sandbox (managed, unit-testable, byte-identity-safe) | The maintainer (Roland) does locally / in CI |
|---|---|
| The **SSIM + per-pixel diff algorithm** (`PixelDiff` — pure, tested with synthetic bitmaps) | Build/run the **pinned-Chrome Docker image** |
| The **diff-runner test** that loads committed `*.png` references + NetPdf output and asserts tolerance (skips cleanly when references are absent) | Run the **reference generator** (Playwright → Chrome print-to-PDF → rasterize) and **commit the reference PNGs** |
| The **PDF→PNG rasterization wrapper** (interface + the PDFium-or-Skia call site) | Install **PDFium** (SkiaSharp can't read PDF) for the NetPdf-side rasterization |
| The **harness skeleton, tolerance constants, CI test stub**, and docs | Wire the **GitHub Actions** Docker diff job + the manual reference-regen workflow |

So the agent lands a PR that is GREEN and complete on the managed side; the visual diff stays inert (skipped) until the maintainer generates references, at which point CI starts enforcing it.

---

## 3. Prerequisites the maintainer installs (you are doing this now)

| Tool | Why | Verify |
|---|---|---|
| **Docker** (Docker Desktop, macOS) | Build/run the pinned-Chrome container (task 30) | `docker --version` |
| **Pinned Playwright/Chrome image** | The reference renderer; pin a Chrome build (NOT `latest`) | e.g. `mcr.microsoft.com/playwright:v1.4x-jammy` pinned to a Chromium revision |
| **Playwright browser** (local path, optional) | Drive Chrome print-to-PDF locally before Dockerizing | `dotnet tool`/`pwsh playwright.ps1 install chromium`, or `npx playwright install chromium` |
| **PDFium** (native) | Rasterize BOTH PDFs → PNG @300 DPI — **SkiaSharp only WRITES pdf, can't read it** | `bblanchon.PDFium` / `PDFiumCore` NuGet, or `pdftoppm`(poppler)/Ghostscript as a fallback |
| **Python 3** (optional) | If `generate-references.py` is Python; a C#/Playwright generator is also fine | `python3 --version` |
| **Pinned font pack** committed to the repo | Both Chrome AND NetPdf must use ONLY these fonts (no system Helvetica/Segoe → avoids platform AA/metric drift) | a `tests/.../fonts/` dir + a FontResolver wired both sides |

**Note — Playwright already pins Chromium** via its NuGet version (each version ships a fixed Chromium revision). Docker is for CI/cross-platform REPRODUCIBILITY; locally `playwright install chromium` + that pinned revision can generate references on macOS, but the COMMITTED references should come from the Docker (Linux) image so CI (Linux) doesn't drift vs macOS AA/fonts.

---

## 4. Task plan (ordered)

Legend: **[A]** = agent/sandbox (lands in the PR, fully tested) · **[M]** = maintainer/local/CI.

1. **[A] `PixelDiff` core** — per-pixel max RGBA-channel delta + SSIM over two equal-size RGBA buffers. Pure function; unit tests with synthetic bitmaps (identical → Δ0/SSIM1; shifted/noised → expected). Tolerance constants `MaxPerPixelDelta = 4`, `MinSsim = 0.98`.
2. **[A] `IPdfRasterizer` + Skia fallback + PDFium adapter seam** — `RasterImage Rasterize(byte[] pdf, int dpi)`. Land the interface + a guard that throws a clear "PDFium not available" when the native lib is absent, so the runner skips rather than crashes. (PDFium is the maintainer install; the seam + tests for the wrapper logic land now.)
3. **[A] Diff-runner test** (`CorpusVisualRegressionTests`) — for each corpus invoice: `HtmlPdf.Convert` → rasterize → load the committed `reference.png` → `PixelDiff` → assert tolerance. **If the reference PNG (or PDFium) is absent → `Skip`** (xUnit 2.9.2 has no `Assert.Skip`; use the project's existing skip pattern / a `[Theory]` member-data that yields nothing → see `project-dev-environment` memory). So the PR is green with zero references.
4. **[A] Harness skeleton + docs** — `tests/NetPdf.RenderingCorpus/docker/` README + the reference-regen runbook (commands, the pinned Chrome version, the font pack), and the `CONTRIBUTING`/phase-4 doc note. Land the `generate-references` SCRIPT (Playwright C# or Python) even though it only RUNS on the maintainer's box.
5. **[M] Docker image (task 30)** — `tests/NetPdf.RenderingCorpus/docker/Dockerfile`, base `mcr.microsoft.com/playwright:…` pinned to a specific Chrome; pinned font pack copied in.
6. **[M] Generate + commit references (tasks 31, 34)** — run the generator in the image for each corpus invoice → 300-DPI PNG → commit under `tests/NetPdf.RenderingCorpus/references/`. The diff-runner [A] stops skipping and starts enforcing.
7. **[A/M] Close visual deltas (task 35)** — run the now-live diff; ABOVE-tolerance mismatches are real engine bugs → agent fixes them in follow-up commits/PRs (gradient stepping, shadow blur σ, font metrics, default margins, sub-pixel placement). DON'T chase sub-threshold pixels (per-pixel Δ < 4 is the contract).
8. **[M] CI wiring (task 33)** — GitHub Actions job runs the Docker diff on every PR; reference-regen is a separate manual workflow.
9. **[A] Release (task 36)** — CHANGELOG `0.9.0-rc1` entry; bump version (`Directory.Build.props` + `version.json`, guarded by `ReleaseVersionParityTests` like `0.7.0-beta`); **maintainer applies the annotated `0.9.0-rc1` git tag**.

---

## 5. Known gotchas (carry into the session)

- **Tailwind CDN corpus files don't render.** `02-tailwind-cdn.html` / `03-tailwind-cdn-responsive.html` need runtime JS to generate the utility CSS (per CLAUDE.md). So the meaningful reference-diffable invoices are **`01-classic-pure-css.html`** and **`04-anvil-running-elements.html`**. Either pre-compile Tailwind to static CSS for 02/03 or exclude them from the visual gate (document the exclusion — "no silent caps").
- **SkiaSharp can't read PDF** — it only writes via `SKDocument`. The NetPdf-side rasterization MUST use PDFium (or poppler/Ghostscript). Don't assume Skia can do it.
- **Platform drift** — macOS vs Linux anti-aliasing + font hinting differ. Commit references from the Docker (Linux) image; run CI on Linux. A pinned font pack used by BOTH renderers is essential.
- **Clean-room** — Chrome is the reference ORACLE only (in the test harness). NetPdf never bundles/links a browser. Keep the harness in the test project, never in `src/`.
- **macOS dev-env constraints** ([[project-dev-environment]]): xUnit 2.9.2 has no `Assert.Skip` (use member-data-yields-nothing or the existing skip idiom); SkiaSharp 3.119 lacks libavif; PNG signature can't use `u8` literals; never `git add -A`; delete `* [0-9].props` shadow files if a gate hits the stale-import warning.
- **`0.9.0-rc1` is internal** (private repo); the PUBLIC flip + NuGet publish happen at `1.0.0` in Phase 5. Don't push anything outward at rc1.

---

## 6. First concrete steps next session

1. Confirm installs: `docker --version`, PDFium NuGet resolves, Playwright browser installed, corpus invoices present.
2. **[A]** Land tasks 1–4 as a normal gated PR (the managed core + skipping diff-runner + skeleton/docs). This is fully doable in-sandbox and leaves the repo green.
3. **[M]** Build the Docker image + generate/commit references for `01` (+ `04`); flip the diff-runner live for those.
4. Iterate task 7 (close deltas) until `01`/`04` pass per-pixel Δ < 4 / SSIM > 0.98.
5. Wire CI (task 8), write the CHANGELOG `0.9.0-rc1` entry + version bump (task 9); maintainer tags `0.9.0-rc1`.

**Then Phase 4 is DONE** (criteria 1-2/9-10 met) and Phase 5 (release) begins.
