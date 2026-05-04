# NetPdf

A pure C# / .NET HTML+CSS-to-PDF rendering engine. **Free, open-source, Apache-2.0.**

NetPdf is a true paged-media renderer written from scratch in managed code. It does **not** wrap a browser engine, **not** spawn a subprocess at render time, **not** depend on revenue-capped or copyleft libraries. It converts a single HTML+CSS string into a deterministic PDF byte stream optimized for documents — invoices, statements, contracts, reports, certificates, catalogs.

**Native dependencies, honestly listed.** NetPdf is "pure managed code" at the engine layer but ships with two permissive-licensed native bundles that Phase 1's tests prove are AOT-clean and reflection-free:

- **HarfBuzzSharp** (MIT) wraps **HarfBuzz** (Old MIT-style). Drives OpenType shaping (kerning, ligatures, RTL, CJK).
- **SkiaSharp** (MIT) wraps **Skia** (BSD-3). Used **only** for image decode (WebP / AVIF / GIF) and the post-Phase-3 raster fallback for filters / blurred shadows / conic gradients. Not a primary graphics path.

Both are loaded as in-process libraries via the official `*.NativeAssets.{Linux,macOS,Win32}` packages — no `Process.Start`, no executable spawning, no IPC. AOT publish + execution is verified on every commit via `scripts/aot-parity.sh`, which asserts the published native binary produces byte-identical PDF output to the JIT path.

> **Status:** Phase 1 ✅ — PDF writer + text foundation shipped (tagged [`0.1.0-alpha`](https://github.com/raroche/NetPdf/releases/tag/0.1.0-alpha) on 2026-05-03). Programmatic PDF construction works end-to-end (deterministic PDF 1.7 bytes, embedded subsetted fonts via WOFF/WOFF2, JPEG/PNG/WebP/AVIF/GIF embedders, full UAX #9/#14/#29 text shaping, AOT-clean with enforced JIT/AOT byte-parity, BenchmarkDotNet baseline with +25% regression gate). The public `HtmlPdf.Convert(html)` facade is **next** — Phase 2 wires HTML+CSS through to the writer (target: `0.3.0-alpha`). First useful release (`0.7.0-beta`) targets static-document rendering. Repository is private until v1.0 launch.

## Why another HTML-to-PDF library?

Every existing free option for .NET fails one or more critical criteria:

| Library | Issue |
|---|---|
| HtmlRenderer.PdfSharp | CSS 2 only, abandoned |
| DinkToPdf / wkhtmltopdf | Abandoned, unpatched CVE-2022-35583, obsolete WebKit |
| QuestPDF | MIT under $1M revenue, commercial above |
| SixLabors.ImageSharp / .Fonts | Same revenue clause |
| iText pdfHTML | AGPL or commercial |
| Syncfusion / IronPDF / Aspose / Spire | Commercial |
| PuppeteerSharp / PlaywrightSharp | Heavy; embeds Chromium |

NetPdf fills the gap: **truly free, truly forever, truly open source.**

## Direction

Closer to **Prince**, **WeasyPrint**, **OpenHTMLtoPDF** than to Playwright/Puppeteer/wkhtmltopdf. Layout-engine first, paint-engine second, PDF byte-writer third — all built from scratch using public W3C / Unicode / ISO specifications under a clean-room policy.

## Public API surface (frozen for v1)

> ⚠️ **At `0.1.0-alpha` (Phase 1) every overload below throws `NotImplementedException`.** The HTML parsing + CSS cascade + layout + paint glue lands in Phase 2 (`0.3.0-alpha`). What ships in Phase 1 is the internal engine — the byte writer, font subsetter, image embedders, and text shaping foundations — exercised end-to-end by the test suite, the AOT smoke binary, and the benchmark gate, but not yet reachable through this facade. The shape below is the v1 contract; calling sites work today but the throws stay until Phase 2.

```csharp
using NetPdf;

// One-liner (throws NotImplementedException at 0.1.0-alpha; works at 0.3.0-alpha+)
var pdf = HtmlPdf.Convert("<h1>Invoice #1234</h1><p>Hello world.</p>");
File.WriteAllBytes("out.pdf", pdf);

// With options
var pdf = HtmlPdf.Convert(html, new HtmlPdfOptions
{
    BaseUri = new Uri("file:///app/templates/"),
    PageSize = PageSize.Letter,
    PrintBackgrounds = true,
});

// Async streaming
await using var fs = File.Create("report.pdf");
await HtmlPdf.ConvertAsync(html, fs, new HtmlPdfOptions { PreferCssPageSize = true });

// Diagnostic mode
var result = HtmlPdf.ConvertDetailed(html);
foreach (var d in result.Warnings) Console.WriteLine($"{d.Code}: {d.Message}");

// What works at 0.1.0-alpha:
Console.WriteLine(HtmlPdf.Version); // -> "0.1.0-alpha+<commit-sha>"
```

See [`docs/compatibility-matrix.md`](docs/compatibility-matrix.md) for the supported / not-supported feature list.
See [`docs/diagnostics-codes.md`](docs/diagnostics-codes.md) for the stable diagnostic code registry.
See [`docs/phases/`](docs/phases/) for per-phase execution guides — what to build, in what order, with exit criteria.

## What's actually shipped at `0.1.0-alpha`

These three columns are the honest answer to "what does NetPdf do today?"

| Capability | Implemented internally now | Reachable through public `HtmlPdf` API now | Wired in |
|---|:---:|:---:|---|
| Deterministic PDF 1.7 byte writer (objects, xref, trailer auto-derived `/ID`, preflight cycle + nested-stream rejection) | ✅ | ❌ | Phase 1 internal; public via Phase 2 |
| Font pipeline: parsing for TTF/OTF/CFF; **embedding for TTF only** (glyph subset → ToUnicode CMap → Type 0/CIDFontType2 wrapper, deterministic 6-letter prefix). CFF subsetting + `FontFile3`/`CIDFontType0C` embedding deferred to a Phase 1.x follow-up. | 🧪 | ❌ | Phase 1 internal; public via Phase 2 |
| WOFF 1.0 (zlib) + WOFF 2.0 (Brotli + glyf/loca transform reverse) | ✅ | ❌ | Phase 1 internal; public via Phase 2 |
| OpenType shaping via HarfBuzzSharp (kerning, ligatures, RTL, CJK) | ✅ | ❌ | Phase 1 internal; public via Phase 2 |
| UAX #9 Bidi (100% UCD), UAX #14 Line Break (99.952% UCD), UAX #29 grapheme clusters (100% UCD) | ✅ | ❌ | Phase 1 internal; public via Phase 2 |
| Liang hyphenation (en-US bundled; other languages ship as `NetPdf.Languages.*` packs at v1.0+) | ✅ | ❌ | Phase 1 internal; public via Phase 2 |
| Image embedders (JPEG passthrough, PNG 4 paths inc. RGBA `/SMask`, WebP/AVIF/GIF via Skia raster) with content-hash dedup | ✅ | ❌ | Phase 1 internal; public via Phase 2 |
| AOT-clean publish + JIT/AOT byte-parity gate | ✅ | n/a (gate, not API) | Phase 1 ships `scripts/aot-parity.sh` |
| Determinism harness (per-platform pinned SHA-256, 72 property tests) | ✅ | n/a (gate, not API) | Phase 1 |
| Performance baseline + +25%-tolerance regression gate | ✅ | n/a (gate, not API) | Phase 1 ships `scripts/benchmark-gate.sh` |
| `HtmlPdf.Convert(html)` — HTML parsing, CSS cascade, computed values, box generation | ❌ | ❌ | Phase 2 (`0.3.0-alpha`) |
| Layout (block/inline/flex/grid/table) + fragmentainer-aware pagination | ❌ | ❌ | Phase 3 (`0.7.0-beta`) |
| Visual-parity hardening (filters via Skia raster fallback, gradients, shadows, full SVG) | ❌ | ❌ | Phase 4 (`0.9.0-rc1`) |

## v1 capability targets (when `1.0.0` ships)

Below is the v1 contract — what `HtmlPdf.Convert(html)` will support once Phases 2–5 land. None of these are reachable through the public API at `0.1.0-alpha`.

**Targeted for v1 support:**
- Block & inline layout, lists, tables (with `<thead>`/`<tfoot>` repetition across pages)
- Web fonts (`@font-face` with TTF/OTF/WOFF/WOFF2), font fallback, OpenType shaping, ligatures, kerning, RTL, CJK
- Images (JPEG passthrough, PNG/Flate, WebP/AVIF via Skia decode)
- Static SVG (shapes, paths, gradients, transforms, text)
- Custom properties (`--*`), `var()`, `calc()`/`min()`/`max()`/`clamp()`
- `@media print`, `@page` (size, margins, margin boxes), `break-*`, `widows`, `orphans`
- `position: absolute`/`fixed` (fixed repeats per page)
- 2D transforms, opacity, gradients, box/text shadows
- Flexbox L1, CSS Grid Level 1
- Filters via subtree raster fallback (blur, drop-shadow, brightness, contrast, etc.)

**Out of scope for v1 (parsed without error; emits structured diagnostics):**
- JavaScript, canvas, `<video>`, `<audio>`, service workers
- 3D transforms, sticky positioning, animations/transitions
- CSS Grid Level 2 (subgrid), container queries, `:has()` rendering, anchor positioning

**Post-v1 roadmap:** tagged PDF (PDF/UA-1) → PDF/A-3u → Grid L2 → container queries → `:has()` → anchor positioning.

## Performance gates (enforced in CI)

- **3-page invoice ≤ 200 ms p50** on commodity desktop.
- **20-page report ≤ 1.5 s p50.**
- **Linear memory growth** with page count.
- **No process spawning at render time.**
- **Native AOT compatible.**
- **Deterministic output:** identical input → identical bytes.

## License

[Apache-2.0](LICENSE).

Third-party attributions: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
