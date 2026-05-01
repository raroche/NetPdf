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
- **Used for:** Image decoding (PNG/JPEG/WebP/AVIF) and **subtree raster fallback** for filters, conic gradients, blurred shadows, complex `clip-path: path()`, masks. Not used in the primary graphics path.
- **Scope discipline:** Code that uses `SkiaSharp` is restricted to `src/NetPdf.Paint/RasterFallback/` and `src/NetPdf.Pdf/Imaging/` by convention. No leakage into layout or text shaping.

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

### PDFium (test-only, via PdfiumViewer or similar wrapper)
- **SPDX:** `BSD-3-Clause`
- **Source:** https://pdfium.googlesource.com/pdfium/
- **Reviewed:** ✅ BSD-3; compatible.
- **Used for:** Independent PDF parser used in `tests/NetPdf.PdfValidation/` for structure validation. Test-only.

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

The `NetPdf.BannedAnalyzer` Roslyn analyzer fails the build if any of these appear in `using` statements or fully-qualified type references inside `src/`.

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

## Adding a new dependency: checklist

1. Open a draft PR titled `Add dependency: <package-name>`.
2. Add an entry to this file with the exact format above.
3. Confirm SPDX in package metadata or upstream LICENSE file.
4. Add the version to `build/Directory.Packages.props` only.
5. Get one maintainer review approving the dossier change.
6. Merge.
7. Only then add `<PackageReference>` in the consuming `csproj`.

---

Last updated: 2026-04-30.
