# NetPdf v1 Compatibility Matrix

This document is the authoritative answer to "does NetPdf support feature X?" Updated as features ship.

**Legend:**
- ✅ Supported — fully implemented and tested.
- 🧪 Partial — implemented with documented caveats (see notes column).
- 📥 Parsed only — CSS grammar accepted; rendering not yet implemented; emits a structured diagnostic with stable code.
- ❌ Out of scope for v1 — not parsed or not rendered; emits diagnostic.

Phase column shows the milestone in which the feature first ships.

---

## HTML

| Feature | Status | Phase | Notes |
|---|---|---|---|
| HTML5 parsing | ✅ | 2 | Via AngleSharp; quirks-mode supported. |
| `<script>` execution | ❌ | — | Collected and emitted as `HTML-SCRIPT-IGNORED-001`. |
| `<style>` & inline `style=""` | ✅ | 2 | |
| `<img>` | ✅ | 4 | JPEG/PNG/WebP/AVIF/GIF (first frame). |
| `<svg>` inline | ✅ | 4 | Static subset; see SVG section. |
| `<a>` hyperlinks | ✅ | 4 | Emitted as PDF `Link` annotations. |
| `<table>` / `<thead>` / `<tbody>` / `<tfoot>` | ✅ | 3 | `<thead>`/`<tfoot>` repeat across pages when `display: table-header-group`/`-footer-group`. |
| `<form>` widgets | ❌ | post-v1 | Tagged for future AcroForm support. |
| `<video>` / `<audio>` | ❌ | — | Out of scope — emits `HTML-MEDIA-UNSUPPORTED-001`. |
| `<iframe>` | ❌ | — | Out of scope — emits `HTML-IFRAME-UNSUPPORTED-001`. |

---

## CSS — Layout

| Feature | Status | Phase | Notes |
|---|---|---|---|
| Block layout (margins, padding, borders, sizing) | ✅ | 3 | Including margin collapsing, BFC. |
| Inline layout (line boxes, vertical-align, line-height, white-space) | ✅ | 3 | |
| Floats (`float`, `clear`) | ✅ | 3 | |
| `position: static` / `relative` / `absolute` | ✅ | 3 | |
| `position: fixed` | ✅ | 3 | Repeated on every page. |
| `position: sticky` | ❌ | post-v1 | Emits `CSS-POSITION-STICKY-UNSUPPORTED-001`. |
| Tables (auto + fixed layout, border-collapse, span) | ✅ | 3 | |
| Multi-column (`column-count`, `column-width`) | ✅ | 3 | |
| Flexbox (CSS Flexible Box Layout L1) | ✅ | 3 | Full L1 spec. |
| CSS Grid (Level 1) | ✅ | 3 | Track sizing with `auto`/`fr`/`minmax`/`fit-content`/`repeat`/`auto-fill`/`auto-fit`; sparse + dense auto-placement; `grid-template-areas`. |
| CSS Grid Level 2 (subgrid) | ❌ | post-v1 | Parsed only; emits `CSS-SUBGRID-UNSUPPORTED-001`. Roadmap v1.3. |

---

## CSS — Paged Media

| Feature | Status | Phase | Notes |
|---|---|---|---|
| `@page` size, margin | ✅ | 3 | |
| `@page :first` / `:left` / `:right` / `:blank` | ✅ | 3 | |
| Page-margin boxes (`@top-left`, `@top-center`, `@top-right`, `@bottom-*`, `@left-*`, `@right-*`) | ✅ | 3 | All 16 boxes. |
| `string()`, `element()`, named pages | ✅ | 3 | |
| `break-before`, `break-after`, `break-inside` | ✅ | 3 | |
| `widows`, `orphans` | ✅ | 3 | Honored by the pagination optimizer's cost model. |
| `<thead>` / `<tfoot>` repetition | ✅ | 3 | |

---

## CSS — Typography

| Feature | Status | Phase | Notes |
|---|---|---|---|
| `font-family`, `font-size`, `font-weight`, `font-style`, `font-stretch` | ✅ | 2 | |
| `@font-face` with TTF, OTF, WOFF, **WOFF2** | ✅ | 1 | WOFF2 decompressed via `System.IO.Compression.BrotliStream` (built into .NET, no extra dep). |
| Web font fetching via `IResourceLoader` | ✅ | 1 | |
| Font fallback chain | ✅ | 1 | |
| OpenType ligatures, kerning | ✅ | 1 | Via HarfBuzz. |
| Bidi (RTL/LTR mixed text) | ✅ | 1 | UAX #9. |
| Complex scripts (Indic, Arabic, Hebrew, CJK, Thai) | ✅ | 1 | Via HarfBuzz; quality varies by script — known limitations documented. |
| Hyphenation (`hyphens: auto`) | 🧪 | 1 | Liang patterns. **At `0.1.0-alpha`: en-US only is bundled** (4,938 patterns + 14 exceptions). Other languages ship as optional `NetPdf.Languages.*` NuGet packs at v1.0+ (Cjk, Indic, European, Arabic, plus an `All` meta-package). See `docs/phases/phase-5-packaging-and-release.md`. |
| `text-align`, `text-decoration`, `text-transform`, `letter-spacing`, `word-spacing` | ✅ | 2 | |
| `writing-mode` (vertical) | 🧪 | 4 | `vertical-rl`/`vertical-lr` supported; sideways modes not. |

---

## CSS — Visual

| Feature | Status | Phase | Notes |
|---|---|---|---|
| `color` (named, hex, rgb, hsl, hwb, lab, lch, oklab, oklch) | ✅ | 2 | All modern color functions. |
| `color-mix()` | ✅ | 2 | |
| `background-color` | ✅ | 3 | |
| `background-image: url(...)` | ✅ | 4 | |
| `background-image: linear-gradient()` | ✅ | 4 | PDF native shading pattern. |
| `background-image: radial-gradient()` | ✅ | 4 | PDF native shading pattern. |
| `background-image: conic-gradient()` | 🧪 | 4 | Skia raster fallback. |
| Multiple backgrounds | ✅ | 4 | |
| `background-size`, `-position`, `-repeat`, `-clip`, `-origin` | ✅ | 4 | |
| `border`, `border-style` (all variants) | ✅ | 3 | |
| `border-radius` | ✅ | 3 | PDF Bezier paths. |
| `border-image` | 🧪 | 4 | Decoded and 9-sliced; complex outsets may differ from Chrome. |
| `box-shadow` (sharp) | ✅ | 4 | Native PDF emit. |
| `box-shadow` (blurred) | 🧪 | 4 | Skia raster fallback. |
| `text-shadow` (sharp / blurred) | 🧪 | 4 | Same as box-shadow. |
| `outline` | ✅ | 3 | |
| `opacity` | ✅ | 4 | PDF ExtGState `/ca`. |
| `mix-blend-mode` | ✅ | 4 | PDF ExtGState `/BM`. |
| `clip-path: rect()` / `inset()` / `polygon()` | ✅ | 4 | Native PDF clipping. |
| `clip-path: path()` | 🧪 | 4 | Skia raster fallback. |
| `mask`, `mask-image` | 🧪 | 4 | Skia raster fallback. |
| `filter: blur` / `drop-shadow` / `brightness` / `contrast` / `saturate` / `sepia` / `hue-rotate` / `invert` / `grayscale` | 🧪 | 4 | Skia raster fallback per filtered subtree. |
| `transform` (2D) | ✅ | 4 | Translate, rotate, scale, skew, matrix. |
| `transform` (3D) | ❌ | — | Emits `CSS-TRANSFORM-3D-UNSUPPORTED-001`. |
| Animations / transitions | ❌ | — | PDF is static. Emits `CSS-ANIMATION-UNSUPPORTED-001`. |

---

## CSS — Modern syntax

| Feature | Status | Phase | Notes |
|---|---|---|---|
| Custom properties (`--*`, `var()`) | ✅ | 2 | |
| `calc()` / `min()` / `max()` / `clamp()` / `abs()` / `sign()` | ✅ | 2 | |
| CSS Nesting (`& { ... }`) | ✅ | 2 | |
| `@layer` cascade layers | ✅ | 2 | |
| `:has()` selector — parsing | ✅ | 2 | Selector compiles. |
| `:has()` selector — rendering | 📥 | post-v1 | Currently treated as no-match; emits `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001`. Roadmap v1.4. |
| `:is()`, `:where()`, `:not()` | ✅ | 2 | |
| Container queries (`@container`) — parsing | ✅ | 2 | |
| Container queries — rendering | 📥 | post-v1 | Emits `CSS-CONTAINER-QUERY-UNSUPPORTED-001`. Roadmap v1.4. |
| Anchor positioning | 📥 | post-v1 | Parsed; emits `CSS-ANCHOR-POSITIONING-UNSUPPORTED-001`. |
| `@media print` | ✅ | 2 | Default media in NetPdf. |
| `@media screen` | ✅ | 2 | Opt-in via `MediaType` option. |
| `@supports` | ✅ | 2 | |

---

## SVG (inline only)

| Feature | Status | Phase | Notes |
|---|---|---|---|
| Shapes (`rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path`) | ✅ | 4 | |
| Fills, strokes, dashes | ✅ | 4 | |
| Linear/radial gradients | ✅ | 4 | |
| 2D transforms | ✅ | 4 | |
| `<text>` | ✅ | 4 | |
| `<image>` (raster) | ✅ | 4 | |
| `<use>` / `<symbol>` / `<defs>` | ✅ | 4 | |
| `<filter>` primitives | ❌ | post-v1 | CSS `filter` covers most needs. |
| `<animate>`, SMIL | ❌ | — | Static document. |
| `<foreignObject>` | ❌ | — | No HTML-in-SVG embedding. |

---

## PDF features

| Feature | Status | Phase | Notes |
|---|---|---|---|
| PDF 1.7 emission | ✅ | 1 | Default. |
| PDF 2.0 emission (xref streams, object streams) | ✅ | 1 | Opt-in via `EmittedPdfVersion = V2_0`. |
| Font subsetting + ToUnicode CMap | ✅ | 1 | Searchable/copyable text. |
| Hyperlinks (Link annotations) | ✅ | 4 | |
| Outlines (bookmarks from headings) | 🧪 | 4 | Opt-in via `Features.GenerateOutlines`. |
| Tagged PDF / PDF/UA-1 | 📥 | post-v1 | Semantic IR built; emission deferred to v1.1. |
| PDF/A-3u | 📥 | post-v1 | Roadmap v1.2. |
| PDF/A-2u | 📥 | post-v1 | Roadmap v1.2. |
| AES-256 encryption | 📥 | post-v1 | Skip RC4 (broken). |

---

## Diagnostic codes

Every ❌ / 📥 / 🧪 entry above corresponds to a stable diagnostic code in `docs/diagnostics-codes.md`. Codes are versioned: once published, a code's meaning never changes; new codes are added for new conditions.

---

## PDF metadata strings

| Feature | Status | Phase | Notes |
|---|---|---|---|
| ASCII `Title` / `Author` / `Subject` / `Keywords` / `Creator` | ✅ | 1 | Emitted as PDF literal strings with §7.3.4.2 octal escaping for `(`, `)`, `\`, and bytes < 0x20 / > 0x7E. |
| Non-ASCII metadata (accented characters, CJK, emoji) | 🧪 | 1 → 2 | The Phase 1 facade exposes `string` setters that feed `PdfLiteralString`, which throws on `char > 0x7E`. Real-world metadata with non-ASCII characters needs the Phase 2 facade to route through UTF-16BE-encoded `PdfHexString`. The byte writer already supports both; the gap is purely at the public surface. |
| Producer string | ✅ | 1 | Always emitted; defaults to `"NetPdf"`. |
| `CreationDate` / `ModDate` | ✅ | 1 | ISO 32000-2 §7.9.4 `D:YYYYMMDDHHmmSS{Z\|+HH'mm'\|-HH'mm'}` format. Default to omitted when not set so output is reproducible. |

---

## Determinism

| Feature | Status | Phase | Notes |
|---|---|---|---|
| Byte-equal output for byte-equal input | ✅ | 1 | Validated by the 75-test harness (`PdfDocumentDeterminismHarnessTests`): 18 document shapes × byte-equal-twice + byte-equal-thrice + structural sanity + per-platform SHA-256 pin. |
| Image dedup by content hash + dictionary | ✅ | 1 | Same image content used N times → single XObject. |
| Pinned `FlateDecode` compression level | ✅ | 1 | `PdfFormat.PdfDeflateCompressionLevel = SmallestSize` is shared by every stream emitter; pins the byte-stability premise of the deflate output. |
| Cross-platform byte-equality | 🧪 | 1 → 5 | Pinned per `OS-arch` key (currently `osx-arm64`); other platforms log "no pin, snapshot skipped" until Phase 5 captures them in the containerized reference environment. |

See [docs/design/determinism.md](design/determinism.md) for the full contract and re-pin protocol.

---

Last updated: 2026-05-03 (Task 23 follow-up review).
