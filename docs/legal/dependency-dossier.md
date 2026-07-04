# Dependency Dossier

Every runtime and test-time dependency of NetPdf, with a written license review. **A dependency may not enter `Directory.Packages.props` without a corresponding entry in this file.**

This file satisfies the clean-room policy (`docs/clean-room-policy.md` §4).

| Status | Meaning |
|---|---|
| ✅ Approved (runtime) | May be referenced from `src/` projects and ships in the `NetPdf` NuGet package. |
| 🧪 Approved (test-only) | May be referenced only from `tests/` projects. Not shipped. |
| ⛔ Banned | Must not be referenced. Enforced by `NetPdf.BannedAnalyzer` (NETPDF0002+). |

---

## ✅ Runtime dependencies

### AngleSharp
- **SPDX:** `MIT`
- **Repository:** https://github.com/AngleSharp/AngleSharp
- **License URL:** https://github.com/AngleSharp/AngleSharp/blob/main/LICENSE
- **Reviewed by:** Phase 0 maintainer review (2026-04-30).
- **Why compatible:** MIT is fully compatible with Apache-2.0 redistribution. AngleSharp ships its own copyright notice; NetPdf preserves it in `THIRD-PARTY-NOTICES.md`.
- **Used for:** HTML5 parsing and DOM only. Configured **without** `IScripting` to keep the DOM static. No execution of `<script>`.
- **Not used for:** layout (we own that), cascade and computed values (we own that), painting, PDF emission.

### AngleSharp.Css
- **SPDX:** `MIT`
- **Repository:** https://github.com/AngleSharp/AngleSharp.Css
- **License URL:** https://github.com/AngleSharp/AngleSharp.Css/blob/devel/LICENSE
- **Reviewed by:** Phase 0 maintainer review (2026-05-01).
- **Why compatible:** MIT, same upstream maintainer as AngleSharp.
- **Used for:** CSS tokenizer + parser + selector match input. We consume the parsed AST through an adapter and run our own cascade and computed-value resolution on top.
- **Why bundled instead of writing our own:** writing a CSS parser is 2-4 weeks of incidental work that doesn't differentiate the engine. Our IP is cascade → computed values → fragmentainer-aware layout → paint → PDF. AngleSharp.Css covers most of CSS 3 today; gaps for `oklch()`, `color-mix()`, container-query syntax, etc. are filled by a thin pre-pass tokenizer extension. v2 stretch: replace if AngleSharp.Css drops behind.

### HarfBuzzSharp (and bundled native HarfBuzz)
- **SPDX:** `MIT` (managed wrapper); `OldMIT` permissive (native HarfBuzz)
- **Repository:** https://github.com/mono/SkiaSharp (HarfBuzzSharp lives in this repo)
- **License URL:** https://github.com/mono/SkiaSharp/blob/main/LICENSE.md and https://github.com/harfbuzz/harfbuzz/blob/main/COPYING
- **Reviewed by:** Phase 0 maintainer review (2026-04-30).
- **Why compatible:** Both licenses are permissive and Apache-2.0-compatible. HarfBuzz's `COPYING` lists multiple sub-license blocks for different files, all permissive.
- **Used for:** OpenType text shaping (ligatures, kerning, RTL, complex scripts). Required for visual fidelity in international text.

### SkiaSharp (and bundled native Skia)
- **SPDX:** `MIT` (managed wrapper); `BSD-3-Clause` (native Skia)
- **Repository:** https://github.com/mono/SkiaSharp
- **License URL:** https://github.com/mono/SkiaSharp/blob/main/LICENSE.md and https://skia.googlesource.com/skia/+/main/LICENSE
- **Reviewed by:** Phase 0 maintainer review (2026-04-30).
- **Why compatible:** Both licenses are permissive and Apache-2.0-compatible.
- **Used for:** (1) Image decoding (PNG/JPEG/WebP/AVIF) and **raster fallback** for filters, conic gradients, **translucent (per-stop alpha) linear / radial gradients** (`LinearGradientRasterizer` / `RadialGradientRasterizer` — `SKShader.Create{Linear,Radial}Gradient` → image + `/SMask`, since a native PDF shading is DeviceRGB; opaque gradients stay native), blurred shadows (box AND text — `TextShadowRasterizer` builds an `SKTypeface` from the embedded font bytes, unions the run's glyph OUTLINES via `SKFont.GetGlyphPath`, and Gaussian-blurs them into an image + `/SMask`), masks, and SVG rasterization; (2) **geometry utilities** in the native vector path — `SKPath.ParseSvgPathData` + path iteration to convert a `clip-path: path("…")` (and SVG `<path>`) string into PDF path operators (`m`/`l`/`c`). The geometry-utility use produces NATIVE PDF vector output (no rasterization); it is a pure, deterministic, AOT-clean transform of an untrusted string into bounded path segments. SkiaSharp's own algorithms are used via its public API (no source was read — clean-room policy preserved).
- **Scope discipline:** `SkiaSharp` use is confined to the rasterization bridges (`NetPdf.Pdf.Images`, `NetPdf.Svg`) and a NARROW geometry seam in the paint bridge — `FragmentPainter.BuildPathClipSegments` (SVG-path → PDF path segments). It MUST NOT leak into layout, text shaping, or the cascade. The clip-path conversion is capped (`MaxClipPathDataLength` / `MaxClipPathSegments`) so an untrusted path can't drive unbounded work.

---

## 🧪 Test-only dependencies (never shipped)

### xUnit
- **SPDX:** `Apache-2.0`
- **Repository:** https://github.com/xunit/xunit
- **Reviewed:** ✅ Apache-2.0; trivially compatible.
- **Used for:** Unit tests in `tests/NetPdf.UnitTests/` and elsewhere.

### BenchmarkDotNet
- **SPDX:** `MIT`
- **Repository:** https://github.com/dotnet/BenchmarkDotNet
- **Reviewed:** ✅ MIT; compatible.
- **Used for:** Performance benchmarks in `tests/NetPdf.Benchmarks/`. Gates CI for performance regressions.

### SharpFuzz
- **SPDX:** `MIT`
- **Repository:** https://github.com/Metalnem/sharpfuzz
- **Reviewed:** ✅ MIT; compatible.
- **Used for:** Mutation-based fuzz testing on input HTML.

### Microsoft.Playwright
- **SPDX:** `Apache-2.0`
- **Repository:** https://github.com/microsoft/playwright-dotnet
- **Reviewed:** ✅ Apache-2.0; compatible.
- **Used for:** Chromium reference renderer in `tests/NetPdf.TestKit/` for visual-regression comparison. Test-only — never linked into the runtime `NetPdf` package.

### PDFium (test-only, via `PDFtoImage`)
- **SPDX:** `BSD-3-Clause` (PDFium native, Google) + `MIT` (`PDFtoImage` managed wrapper) + `Apache-2.0` (bblanchon PDFium native-asset packaging)
- **Source:** https://pdfium.googlesource.com/pdfium/ · https://github.com/sungaila/PDFtoImage · https://github.com/bblanchon/pdfium-binaries
- **Reviewed:** ✅ BSD-3 / MIT / Apache-2.0 are all permissive + Apache-2.0-compatible. Test-only.
- **Package:** `PDFtoImage` (test-only; pulls `bblanchon.PDFium.{macOS,Linux,Win32}` native assets — cross-platform, no separate install). Referenced only from `tests/NetPdf.RenderingCorpus/`. Its SkiaSharp floor (`3.119.2`) is the **repo-wide** SkiaSharp pin, so the visual harness measures the SAME renderer dependency set as production (the whole solution is on SkiaSharp `3.119.2`; the byte-identity gates were re-verified after the `3.119.0 → 3.119.2` patch bump).
- **Used for:** the **visual-regression harness (PR 8)** in `tests/NetPdf.RenderingCorpus/` — rasterizes the NetPdf PDF (and, on the maintainer box, the Chrome reference PDF) to RGBA at 300 DPI for `PixelDiff` (SkiaSharp only WRITES PDF, so a PDFium reader is required). Never linked into the runtime `NetPdf` package. (PDF-structure validation in `tests/NetPdf.PdfValidation/` uses the `qpdf` CLI, below — not PDFium.)

### qpdf
- **SPDX:** `Apache-2.0`
- **Source:** https://qpdf.sourceforge.io/
- **Reviewed:** ✅ Apache-2.0; compatible.
- **Used for:** External CLI tool invoked in CI for PDF structure validation. Not linked.

### veraPDF (CI-only external tool)
- **SPDX:** `GPL-3.0` (Java)
- **Source:** https://verapdf.org/
- **Reviewed:** ⚠️ GPL-3.0. Used **only** as an external command-line tool in CI for PDF/A validation. NetPdf does **not** link, embed, or distribute veraPDF. Per GPL-3.0, invoking a separate process is not derivative-work creation. Compatible with our Apache-2.0 distribution as long as we never bundle or static-link it.
- **Used for:** Post-v1 PDF/A conformance gating in CI.

---

## ⛔ Banned (in runtime path)

None of these may appear in `src/`. Enforcement is by **process** — the dependency-dossier review gate (adding a `PackageVersion` requires a reviewed entry here first), code review, and the CI vulnerable-dependency scan (below). There is no bespoke `NetPdf.BannedAnalyzer` today; machine-enforcement via `Microsoft.CodeAnalysis.BannedApiAnalyzers` + a `BannedSymbols.txt` (starting with `System.Drawing.*`, which the codebase already avoids) is a documented future option.

| Package / Namespace | Reason |
|---|---|
| `System.Drawing.*` | Windows-only; deprecated in .NET Core; unsuitable for cross-platform. |
| `Microsoft.Web.WebView2.*` | Embeds a browser engine. |
| `PuppeteerSharp.*` / `Microsoft.Playwright.*` (in `src/`) | Browser automation. (Playwright permitted in `tests/`.) |
| `iText.*` / `iTextSharp.*` | AGPL or commercial. |
| `QuestPDF.*` | MIT under $1M revenue, commercial above — incompatible with "100% free forever." |
| `SixLabors.*` (ImageSharp, Fonts) | Same revenue clause. |
| `PdfSharp.*` / `PdfSharpCore.*` | Although MIT, NetPdf builds its own PDF writer; depending on PdfSharp would defeat the design. Listed here to prevent accidental imports. |
| `Spire.*`, `Aspose.*`, `Syncfusion.*`, `IronPdf.*`, `EvoPdf.*`, `SelectPdf.*` | Commercial. |
| `FriBidi.*` (any LGPL bindings) | LGPL incompatible with our static-link distribution model. |
| `Ghostscript.*` | AGPL/commercial dual-licensed; complex compliance. |

---

## Native-dependency CVE policy & patch cadence (SEC-10)

The residual remote-code-execution surface (threat-model class **V5**) is the **native code** reached
with attacker-controlled bytes: Skia's image codecs, HarfBuzz's font shaper, and (test-only) PDFium.
NetPdf's pre-decode validators (`ImageSafetyValidator`, `FontSafetyValidator`) bound the *shape* of what
reaches them, but a memory-safety 0-day in the native library itself (cf. libwebp **CVE-2023-4863**,
neodyme "HTML renderer to RCE") is mitigated **primarily by staying patched**. Policy:

- **Version floors (current pins).** SkiaSharp `3.119.2` (+ matching `NativeAssets.*`), HarfBuzzSharp
  `8.3.1.1` (+ `NativeAssets.*`), AngleSharp `1.1.2`, AngleSharp.Css `1.0.0-beta.144`. Test-only:
  PDFtoImage/`bblanchon.PDFium`, Microsoft.Playwright, SharpFuzz, xUnit, BenchmarkDotNet. Never float a
  native package below its pinned floor; the whole solution moves together (SkiaSharp is repo-wide so the
  visual harness measures production's renderer set).
- **Monitoring.** CI runs `dotnet list package --vulnerable --include-transitive` on every PR (the
  `dependency-scan` job in [`.github/workflows/fuzz-smoke.yml`](../../.github/workflows/fuzz-smoke.yml)) and
  **fails** the build if any advisory matches. GitHub Dependabot / advisory alerts watch the same graph.
- **Patch cadence.** Apply a security patch to a native dependency **promptly** (target: within one release
  cycle of an advisory affecting a pinned version; sooner for Critical/High reaching a decoder). Because a
  Skia/HarfBuzz bump can change rendered bytes, **re-verify byte-identity** after any native bump per
  [`docs/design/determinism.md`](../design/determinism.md) (the golden/corpus/AOT-parity gates) and re-pin
  the determinism hash if it legitimately moved.
- **Defense in depth.** The library cannot substitute for OS-level isolation against a native 0-day — the
  untrusted-HTML deployment MUST additionally run sandboxed (see [`deployment.md`](../security/deployment.md),
  SEC-11).

---

## Adding a new dependency: checklist

1. Open a draft PR titled `Add dependency: <package-name>`.
2. Add an entry to this file with the exact format above.
3. Confirm SPDX in package metadata or upstream LICENSE file.
4. Add the version to `build/Directory.Packages.props` only.
5. Get one maintainer review approving the dossier change.
6. Merge.
7. Only then add `<PackageReference>` in the consuming `csproj`.

---

Last updated: 2026-07-04 (SEC-10 — added the native-dependency CVE policy & patch cadence + the CI vulnerable-dependency scan; corrected the `NetPdf.BannedAnalyzer` claim).
