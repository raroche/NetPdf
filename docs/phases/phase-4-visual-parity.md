# Phase 4 — Visual Parity Hardening

**Status:** ⏳ pending (after Phase 3).
**Time:** team estimate 10 wk → Claude Opus 4.7 high: **3–5 wk**.
**Tagged release:** `0.9.0-rc1` — visual fidelity locked.

## Goal

Polish the painter and image pipeline so the output matches a Chromium print reference within tight tolerance. Add 2D transforms, gradients (linear/radial PDF-native; conic raster), shadows, all CSS filters via Skia raster fallback, and a static SVG subset. Stand up the containerized visual-regression harness with pinned Chrome + pinned font pack so tolerance is measurable and stable.

## Prerequisites

- Phases 0–3 complete; `0.7.0-beta` tagged.
- Read [phase-3-layout-and-pagination.md](phase-3-layout-and-pagination.md) — `FragmentTree` and `DisplayList` are the inputs.
- Read [../compatibility-matrix.md](../compatibility-matrix.md) — every visual entry marked `✅ Phase 4` is in scope.
- Read [../diagnostics-codes.md](../diagnostics-codes.md) — every raster-fallback code (`CSS-FILTER-RASTER-FALLBACK-001` etc.) emits from this phase.

## Deliverables

### Native PDF graphics — `NetPdf.Paint` + `NetPdf.Pdf`

For each feature: emit native PDF graphics ops where possible; fall back to Skia raster only when PDF can't represent the effect.

#### 2D transforms
- `transform: translate / rotate / scale / skew / matrix` → PDF `cm` (concatenate matrix).
- 3D transforms (`translateZ`, `rotateX`, `perspective`, etc.) flattened to 2D projection; emit `CSS-TRANSFORM-3D-UNSUPPORTED-001` warning.

#### Gradients
- **Linear gradient**: PDF native shading pattern Type 2 (axial). `linear-gradient(angle, stops...)` → `Coords` + `Function` + `Extend`.
- **Radial gradient**: PDF native shading pattern Type 3.
- **Conic gradient**: NO PDF native equivalent — Skia raster fallback (`CSS-CONIC-GRADIENT-RASTER-001`).
- **Repeating gradients**: PDF shading + tiling pattern, or raster fallback for complex cases.
- Multi-stop, hint-position, and color-interpolation hints (CSS Color L4) supported per spec.

#### Borders
- Solid, dashed, dotted, double, groove, ridge, inset, outset → PDF native paths with `d` (dash pattern), correct miter joins.
- `border-radius` → PDF Bezier paths (cubic approximation of quarter-arc, control-point factor `k = 0.5523`).
- `border-image` → 9-slice decode + emit as native fills/images.

#### Backgrounds
- `background-color` → PDF native fill.
- `background-image: url(...)` → image XObject reference; positioning per `background-position`/`size`/`repeat`/`clip`/`origin`.
- Multiple backgrounds → emit in spec order (first listed is on top).

#### Box-shadow / text-shadow
- **Sharp shadows** (`blur = 0`): PDF native rect/path with offset.
- **Blurred shadows**: Skia raster fallback (`CSS-BOXSHADOW-BLUR-RASTER-001`, `CSS-TEXTSHADOW-BLUR-RASTER-001`). Cap raster size at 4096 px max dim with adaptive DPI for large regions.

#### Filters (Skia raster only)
- `filter: blur() / drop-shadow() / brightness() / contrast() / saturate() / sepia() / hue-rotate() / invert() / grayscale() / opacity()` → rasterize the filtered subtree at `DevicePixelRatio × 96` DPI, embed as PNG, emit `CSS-FILTER-RASTER-FALLBACK-001`.
- `filter: opacity()` is a special case — also expressible as PDF native `/ca` ExtGState; prefer native.

#### Clip-path / Mask
- `clip-path: rect() / inset() / polygon()` → PDF native clipping `W` operator.
- `clip-path: path()` → Skia raster (`CSS-CLIP-PATH-RASTER-FALLBACK-001`).
- `mask`/`mask-image` → Skia raster compositing (`CSS-MASK-RASTER-FALLBACK-001`).

#### Opacity / blend modes
- `opacity` → PDF ExtGState `/ca` (non-stroking) and `/CA` (stroking).
- `mix-blend-mode` → PDF ExtGState `/BM` per CSS Compositing L1.

#### Hyperlinks & outlines
- `<a href>` → PDF `Link` annotations on the bounding box of the link's content.
- Outlines (PDF bookmarks) — opt-in via `Features.GenerateOutlines`. Generate from `<h1>`–`<h6>` heading hierarchy.

### Image pipeline

- **JPEG**: passthrough via `DCTDecode` (no re-encode, fastest path).
- **PNG**: decode via SkiaSharp → re-emit via `FlateDecode` with `SMask` for alpha. PNG predictor where beneficial.
- **WebP**: decode via SkiaSharp → emit as PNG-style.
- **AVIF**: decode via SkiaSharp → emit as PNG-style.
- **GIF**: first frame only via SkiaSharp; static.
- **SVG**: handled by `NetPdf.Svg` (next deliverable), not via raster.
- **CMYK preservation**: when input image is CMYK, embed as DeviceCMYK without RGB conversion.

### `NetPdf.Svg` — static SVG renderer

Inline `<svg>` only (SVG-in-`<img>` decoded via SkiaSharp as a raster image).

- **Shapes**: `<rect>`, `<circle>`, `<ellipse>`, `<line>`, `<polyline>`, `<polygon>`, `<path>`.
- **Paths**: full SVG path data syntax (`M`, `L`, `H`, `V`, `C`, `S`, `Q`, `T`, `A`, `Z` and lowercase variants).
- **Fills & strokes**: `fill`, `stroke`, `stroke-width`, `stroke-dasharray`, `stroke-linecap`, `stroke-linejoin`, `stroke-miterlimit`, `fill-rule`.
- **Gradients**: `<linearGradient>`, `<radialGradient>` with `<stop>` elements.
- **Transforms**: `transform="translate() rotate() scale() skew() matrix()"`.
- **Text**: `<text>`, `<tspan>` rendered using NetPdf's text shaping pipeline.
- **`<use>` / `<symbol>` / `<defs>`**: definitions and instantiation.
- **Image**: `<image>` raster embedding.
- **Out of scope (post-v1)**: `<filter>` primitives, SMIL animations, `<foreignObject>` HTML embedding.

### Pinned visual-regression harness

This is the testing infrastructure that makes visual fidelity measurable.

- **Docker image** at `tests/NetPdf.RenderingCorpus/docker/Dockerfile`:
  - Base: `mcr.microsoft.com/playwright:focal` (or similar) pinned to a specific Chrome version (e.g., 130.0.x).
  - Installs Liberation, DejaVu, Noto font families pinned to specific versions.
  - Installs Playwright, Pillow (for diff), Python 3.
- **Reference generator** at `tests/NetPdf.RenderingCorpus/scripts/generate-references.py`:
  - For each HTML in `tests/NetPdf.RealDocuments/Corpus/`, run inside the Docker image, drive Chrome to print the page to PDF, then rasterize that PDF to PNG at 300 DPI.
  - Output: `tests/NetPdf.RenderingCorpus/References/<corpus-file>.png` — committed to the repo.
- **Diff runner** at `tests/NetPdf.RenderingCorpus/PixelDiffRunner.cs`:
  - For each corpus file, render via NetPdf, rasterize the resulting PDF to PNG (using PDFium or Skia), pixel-diff against the committed reference.
  - Tolerance: per-pixel RGBA delta < 4, SSIM > 0.98.
  - On mismatch, emit a diff image to `TestResults/diffs/`.
- CI runs the diff runner on every PR. Reference regeneration is a separate manual workflow (so upstream Chrome/font drift never silently changes tests).

## Spec references

| Topic | Source |
|---|---|
| CSS Transforms L1 | https://www.w3.org/TR/css-transforms-1/ |
| CSS Backgrounds & Borders L3 | https://www.w3.org/TR/css-backgrounds-3/ |
| CSS Images L3 | https://www.w3.org/TR/css-images-3/ |
| CSS Filter Effects L1 | https://www.w3.org/TR/filter-effects-1/ |
| CSS Compositing L1 | https://www.w3.org/TR/compositing-1/ |
| CSS Masking L1 | https://www.w3.org/TR/css-masking-1/ |
| CSS Shapes L1 | https://www.w3.org/TR/css-shapes-1/ |
| SVG 1.1 | https://www.w3.org/TR/SVG11/ |
| SVG 2 | https://svgwg.org/svg2-draft/ |
| PDF shading patterns | ISO 32000-2 §8.7 |
| PDF transparency | ISO 32000-2 §11 |

## Current status (2026-06-25)

Phase 4 is the **active phase** (~15-20% done). **Merged:** 2D transforms (#210), linear + radial gradients (#209), sharp + blurred box-/text-shadow (#210), backgrounds + `object-fit`/`-position` (Phase-3 interleaved), `border-radius` Bézier corners (Phase-3). The **Skia raster-fallback bridge** is established (`ShadowRasterizer` → image XObject + alpha `/SMask` + a once-per-conversion diagnostic) — conic gradients, filters, blurred effects, complex clip-path, and masks all reuse it.

**Not started (greenfield):** `filter` / `clip-path` / `mask` / `opacity` / `mix-blend-mode` / `border-image` aren't registered in `properties.json`; `NetPdf.Svg` is a single `Placeholder.cs`; there are no Link annotations, document outlines, conic / repeating gradients, faithful non-solid border styles (currently approximated-as-solid + diagnosed), or visual-regression harness. The remaining work is grouped into 8 ordered 3-task PR groups in [PROGRESS.md](../../PROGRESS.md#phase-4--remaining-work-roadmap-active-phase-2026-06-25). Rows marked ✅ below are merged.

## Work breakdown (ordered)

| # | Task | Mini-est. | Depends on |
|---|---|---|---|
| 1 | ✅ **2D transforms (CTM emission)** — MERGED #210 | 2 d | — |
| 2 | ✅ **Linear gradient → PDF shading Type 2** — MERGED #209 | 2 d | — |
| 3 | ✅ **Radial gradient → PDF shading Type 3** — MERGED #209 | 2 d | 2 |
| 4 | Conic gradient → Skia raster | 2 d | 2 |
| 5 | Border styles (incl. dashed/double/etc.) | 3 d | — |
| 6 | ✅ **`border-radius` Bezier corners** — MERGED (Phase-3) | 1 d | 5 |
| 7 | `border-image` 9-slice | 2 d | 5 |
| 8 | ✅ **Background painting (color, image, position/size/repeat)** — MERGED (Phase-3 interleaved); `border-image` / multi-layer refinements remain | 3 d | — |
| 9 | ✅ **Sharp box-shadow / text-shadow** — MERGED #210 (inset deferred) | 1 d | — |
| 10 | ✅ **Blurred shadow Skia raster fallback** — MERGED #210 (`ShadowRasterizer`) | 2 d | 9 |
| 11 | CSS filters via Skia raster (all 9 filter functions) | 4 d | — |
| 12 | `filter: opacity()` → native ExtGState path | 0.5 d | 11 |
| 13 | `clip-path: rect/inset/polygon` native | 1 d | — |
| 14 | `clip-path: path()` raster fallback | 2 d | 13 |
| 15 | `mask`/`mask-image` raster fallback | 2 d | 14 |
| 16 | `mix-blend-mode` ExtGState | 1 d | — |
| 17 | Hyperlink Link annotations | 1 d | — |
| 18 | Outlines from `<h1>`–`<h6>` | 1 d | — |
| 19 | Image pipeline: JPEG passthrough refinement | 0.5 d | — |
| 20 | Image pipeline: PNG/Flate + SMask refinement | 1 d | 19 |
| 21 | Image pipeline: WebP/AVIF via Skia | 1 d | 20 |
| 22 | Image pipeline: GIF first-frame | 0.5 d | 21 |
| 23 | Image pipeline: CMYK preservation | 1 d | 19 |
| 24 | SVG parser + AST | 2 d | — |
| 25 | SVG shapes + paths + transforms | 4 d | 24 |
| 26 | SVG fills/strokes/dashes | 1 d | 25 |
| 27 | SVG gradients | 1 d | 25 |
| 28 | SVG `<text>` integration with text shaping | 2 d | 25 |
| 29 | SVG `<use>`/`<symbol>`/`<defs>` | 2 d | 25 |
| 30 | Pinned-Chrome Docker image | 2 d | — |
| 31 | Reference PNG generator script | 1 d | 30 |
| 32 | Pixel-diff runner (SSIM + per-pixel) | 2 d | 30 |
| 33 | CI integration of diff runner | 1 d | 32 |
| 34 | Generate references for full corpus | 1 d | 31, 32 |
| 35 | Close largest visual deltas across corpus | 5 d | 34 |
| 36 | Tag `0.9.0-rc1` + CHANGELOG | 0.5 d | all |

**Total: ~62 days. With Claude Opus 4.7 high + daily Roland review: 3–5 calendar weeks.**

## Implementation notes

### Native vs raster decision rule
1. Try PDF-native first (smallest file, vector quality, infinite zoom).
2. If PDF can't represent it (filters, conic gradients, complex clip-path, masks, blurred shadows), rasterize the affected subtree only.
3. Cap raster bitmap size at 4096 px max dimension; downsample if larger.
4. Adaptive DPI: lower for large regions to bound output size.
5. Always emit the corresponding `CSS-*-RASTER-*` diagnostic so users know what's not vector.

### PDF shading patterns
The `Function` field of a shading dictionary can be:
- `FunctionType 2` — exponential (good for two-stop linear gradients).
- `FunctionType 3` — stitching (chains multiple Type 2 functions for multi-stop).
- For an N-stop linear gradient: use Type 3 stitching N-1 Type 2 sub-functions.

### Skia raster fallback bridge
```csharp
internal static class SkiaRasterizer
{
    public static RasterImage Rasterize(DisplayList subtree, RectF bounds, float dpi)
    {
        var pixelW = (int)Math.Min(bounds.Width * dpi / 96, 4096);
        var pixelH = (int)Math.Min(bounds.Height * dpi / 96, 4096);
        using var bitmap = new SKBitmap(pixelW, pixelH);
        using var canvas = new SKCanvas(bitmap);
        // Replay subtree commands onto canvas
        // ...
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return new RasterImage(data.ToArray(), pixelW, pixelH);
    }
}
```
Embed the resulting PNG as a single `ImageDraw` command in the parent display list.

### Pinned Chrome version
Pick a specific Chrome version (e.g., `130.0.6723.91` as of late 2024) and pin it via the Playwright Docker image tag. **Don't use `latest`.** Reference PNGs are tied to that exact Chrome; upgrading Chrome regenerates references in a controlled workflow.

### Pinned font pack
Bundled fonts (in repo, under `tests/NetPdf.RenderingCorpus/fonts/`):
- **Liberation Sans / Serif / Mono** (SIL OFL — Latin coverage matching Helvetica/Times/Courier metrics).
- **DejaVu Sans / Serif / Mono** (Bitstream Vera derivative — broader Latin/Greek/Cyrillic coverage).
- **Noto Sans / Serif / CJK / Devanagari / Arabic / Hebrew** (SIL OFL — internationalization).

Both NetPdf and the Chrome reference renderer use **only** these fonts. Common system fonts (Helvetica on macOS, Segoe UI on Windows) deliberately not used to avoid platform drift.

### Visual-diff tolerance
- **Per-pixel RGBA delta < 4**: tolerates anti-aliasing differences.
- **SSIM > 0.98**: structural similarity catches issues per-pixel diff misses.
- **Failures emit a diff PNG** to `TestResults/diffs/<corpus-file>.diff.png` so reviewing a regression is one click.

## Test plan

| Component | Test | Location |
|---|---|---|
| 2D transforms | Snapshot | `tests/NetPdf.RenderingCorpus/Transforms/` — translate/rotate/scale/skew matrix outputs. |
| Linear gradient | Reference | Per CSS Images L3 examples. |
| Radial gradient | Reference | Per CSS Images L3 examples. |
| Conic gradient | Reference | Mark as raster fallback in result; visual diff. |
| Border styles | Reference | All 9 border-style values. |
| `border-radius` | Reference | Single-corner, two-value, four-value. |
| Backgrounds | Reference | Multiple bgs, position keywords, size cover/contain. |
| Sharp shadows | Reference | offset only, no blur. |
| Blurred shadows | Reference | various blur radii; raster fallback engaged. |
| Filters | Reference | All 9 filter functions; chained filters. |
| Clip-path | Reference | rect/inset/polygon native; path() raster. |
| Mask | Reference | mask-image with alpha. |
| Hyperlinks | Integration | Click-region in PDFium matches expected URL. |
| Outlines | Integration | Open in Acrobat Reader; bookmark hierarchy matches `<h1>`–`<h6>`. |
| Image pipeline | Round-trip | Embed every supported format; pixel-diff vs source. |
| SVG shapes | Reference | Basic shapes with all stroke/fill attributes. |
| SVG paths | Reference | Complex paths from real-world SVG icons. |
| SVG gradients | Reference | Linear + radial. |
| SVG text | Reference | Text on path, tspan positioning. |
| Real corpus | Reference | All 4 corpus files; visual diff vs committed Chrome reference. |
| Performance | Benchmark | Phase 3 targets must hold (no regression). |

## Exit criteria

Phase 4 is complete when:

1. 🔶 All visual-regression diffs against pinned-Chrome references pass within tolerance (per-pixel < 4, SSIM > 0.98). — **Gate machinery complete** (PDFium rasterizer + `PixelDiff` + `VisualGatePolicy`, which auto-activates once references exist); **MAINTAINER STEP**: commit the canonical per-page reference PNGs generated on Linux (see criterion 9) under `tests/NetPdf.RenderingCorpus/references/`. Inert until they land.
2. 🔶 All 4 invoice corpus files match Chrome reference PNGs within tolerance. — Same maintainer step as (1); the C# oracle (`ChromeReferenceGenerator`) is validated end-to-end in-sandbox (a solid box pixel-matches Chrome, maxΔ 0 / SSIM 1).
3. ✅ Conic gradients, blurred shadows, all filters, complex clip-path, masks all engage raster fallback with the correct diagnostic codes.
4. ✅ Static SVG corpus renders correctly.
5. ✅ Hyperlinks survive a round-trip through PDFium.
6. ✅ Optional outlines materialize as PDF bookmarks.
7. ✅ Performance: no regression vs Phase 3 targets.
8. ✅ AOT smoke + determinism still pass.
9. ✅ Pinned-Chrome Docker image is reproducibly buildable; reference regeneration is a documented manual workflow (`tests/NetPdf.RenderingCorpus/docker/`).
10. 🔶 CHANGELOG updated, `0.9.0-rc1` tagged. — CHANGELOG **updated + `0.9.0-rc1` staged** (`Directory.Build.props` + `build/version.json` + heading, guarded by `ReleaseVersionParityTests`); the annotated git tag is applied by the maintainer after the closeout PR merges (same protocol as `0.7.0-beta`).

> **Closeout status (2026-07-01):** the rc1 release is STAGED (this doc + CHANGELOG + version surfaces). Criteria 3–9 are met in-code and CI-gated. The only work between here and a fully-green Phase 4 is the two **maintainer/CI** actions above: (a) generate + commit the Linux canonical reference PNGs → flips criteria 1–2 green + activates the enforcing gate, and (b) apply the `0.9.0-rc1` git tag → closes criterion 10. No further engine changes are required to exit the phase; the deferred IPaintTarget group-compositing epic and the native-SVG subset extension are post-rc1 quality work (`docs/deferrals.md`), not exit criteria.

## Common pitfalls

- **Chasing pixel-perfection.** "Per-pixel RGBA delta < 4" is the contract. Don't grind on differences below the threshold.
- **Over-rasterizing.** Native PDF is always smaller and infinitely zoomable. Default to native; raster only when forced.
- **Conic gradients via repeating linear.** Tempting hack; produces visible banding. Just rasterize.
- **Border-radius ellipse approximation error.** The `k = 0.5523` quarter-arc factor minimizes error to < 0.05% — don't second-guess it.
- **Box-shadow interpretation.** "spread" expands the box outline, "blur" softens. They compose. Read CSS B&B L3 §7 carefully.
- **Multiple backgrounds order.** First in declaration is on top (opposite of stacking-context convention). Test with overlapping colors.
- **Filter `drop-shadow` vs `box-shadow`.** `drop-shadow` follows the alpha channel of the filtered content (text glyphs, image alpha); `box-shadow` follows the rectangular box. Different rendering paths.
- **CMYK image embedding.** A CMYK JPEG must declare `/ColorSpace /DeviceCMYK` and `/Decode [1 0 1 0 1 0 1 0]` (inverted because CMYK 0=ink-on, 1=ink-off in PDF).
- **SVG path arc command.** The `A` arc command's parameters (`rx ry x-axis-rotation large-arc-flag sweep-flag x y`) trip implementers up. Use the SVG implementation note for elliptical arc parameter conversion.
- **SVG `<use>` cycles.** A `<use>` referencing its own `<symbol>` ancestor is a parse error; detect and emit a diagnostic.
- **Reference PNGs out-of-date.** When you intentionally change rendering, regenerate references in a separate commit so review can verify the visual change.
- **CI flakiness from font hinting.** Even with pinned fonts, FreeType vs Skia hinting differs. Apply diff tolerance globally; resist tightening it.

## Hand-off to Phase 5

State of the repo at end of Phase 4:
- Visual fidelity is locked. The 4 corpus files render within Chrome diff tolerance.
- All v1-scoped CSS visual features work.
- Static SVG renders correctly.
- Pinned visual-regression harness is in place; references committed.
- Phase 5 ships the package: cross-platform CI, language packs, documentation site, sample apps polished, BenchmarkDotNet baselines locked.
