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
| Multi-column (`column-count`, `column-width`) | 🧪 | 3 | Partial / approximated per Phase 3 Task 14 cycle 1. `column-count` ships (equal-column split, serial fill, forced-overflow diagnostic). `column-width` parsed + cascaded but unused at layout time (cycle 2+ ships derivation). Column balancing (`column-fill: balance`), `column-span: all`, painted column rules, multi-page multicol (the multicol container fragmenting across pages) all deferred. See [docs/deferrals.md#multicol-balancing-pagination](deferrals.md#multicol-balancing-pagination). |
| Flexbox (CSS Flexible Box Layout L1) | ✅ | 3 | Full L1 spec. |
| CSS Grid (Level 1) | ✅ | 3 | Track sizing with `auto`/`fr`/`minmax`/`fit-content`/`repeat`/`auto-fill`/`auto-fit`; sparse + dense auto-placement; `grid-template-areas`. |
| CSS Grid Level 2 (subgrid) | ❌ | post-v1 | Parsed only; emits `CSS-SUBGRID-UNSUPPORTED-001`. Roadmap v1.3. |

---

## CSS — Paged Media

| Feature | Status | Phase | Notes |
|---|---|---|---|
| `@page` size, margin | ✅ | 3 | Named/orientation/`<length>` size + per-side margins; `!important`, paper-size-`@media` ignore (§3.3), `%` margins vs. the resolved page box. |
| `@page :first` / `:left` / `:right` / `:blank` | 🧪 | 3 | `@page :first` is APPLIED — on the single (first) page it overrides the bare `@page` (margin / size / margin boxes) by cascade specificity, via `AtPageRules.EnumeratePageRules` yielding bare-then-`:first` (a bare `!important` still beats a `:first` normal). `:left` / `:right` / `:blank` + named pages are recognized but DEFERRED (they need the multi-page driver's page context) ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| Page-margin boxes (`@top-left`, `@top-center`, `@top-right`, `@bottom-*`, `@left-*`, `@right-*`) | 🧪 | 3 | All 16 boxes: literal + `attr()` + `counter(page)`/`counter(pages)` (incl. `, decimal`) content; per-box style (`font`/`font-*` incl. relative sizes / `color` / `text-align` / `vertical-align` / `background-color` / `border` + per-side `border-<side>` + the `border-width`/`-style`/`-color` box shorthands / `padding` + the border content-inset) + page-context→root inheritance + `@page :first` + §5.3 box sizing (shrink-to-fit + explicit `width`/`height` + sibling-overlap distribution with min/max-content flex + content overflow wrapping) + `string(name)` (via `string-set`) + `element(name)` (via `position: running()`) content (Task 21 cycles 3–16 + Tasks 22–23). Non-page `counter()` / unsupported counter styles / `counters()` content, `string-set: … content()` (AngleSharp drops it — needs a raw-CSS pre-pass), `element()` full block rendering + cross-page running, `border-radius`, non-absolute (`%`/`calc()`/`em`) padding, wrapped-line content-alignment, vertical-edge (height) overflow, `box-sizing` deferred ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `string()` / `string-set`, `element()` / `position: running()`, named pages | 🧪 | 3 | FIRST CUT (single page): `string(name)` resolves a `string-set` (literal + `attr()` content-lists, one-or-more comma-separated pairs); `element(name)` resolves a `position: running()` element's text (removed from normal flow) (Tasks 22–23). DEFERRED: `string-set: … content()` (raw-CSS pre-pass), `element()` full block rendering, cross-page "running" persistence, and named-page selectors (multi-page-gated) ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `break-before`, `break-after`, `break-inside` | ✅ | 3 | |
| `widows`, `orphans` | ✅ | 3 | Honored by the pagination optimizer's cost model. |
| `<thead>` / `<tfoot>` repetition | ✅ | 3 | |

---

## CSS — Typography

| Feature | Status | Phase | Notes |
|---|---|---|---|
| `font-family`, `font-size`, `font-weight`, `font-style`, `font-stretch` | ✅ | 2 | |
| `@font-face` with TTF, OTF, WOFF, **WOFF2** | 🧪 | 1 | All four formats **parsed** in Phase 1; WOFF2 decompressed via `System.IO.Compression.BrotliStream` (built into .NET, no extra dep). **Embedding scope:** TTF only at `0.1.0-alpha`. OTF/CFF embedding (`FontFile3` / `CIDFontType0C`) deferred to Phase 1.x — `EmbeddedTtfFont.Build` throws explicitly on CFF-flavored fonts rather than producing malformed PDF. |
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
| `color` (named, hex, rgb, hsl) | ✅ | 2 | Task 10 cycle 1's [`ColorResolver`](../src/NetPdf.Css/ComputedValues/PropertyResolvers/ColorResolver.cs) computes typed values from named (147 entries via [`CssNamedColors`](../src/NetPdf.Css/ComputedValues/PropertyResolvers/CssNamedColors.cs)), every hex form (`#rgb`/`#rgba`/`#rrggbb`/`#rrggbbaa`), `rgb()`/`rgba()` and `hsl()`/`hsla()` in BOTH legacy comma + modern whitespace + slash-alpha syntax. Strict syntax separation per CSS Color 4 §4.2 — mixed comma + slash forms emit `CSS-PROPERTY-VALUE-INVALID-001`. |
| `color` (system colors — `canvas`, `canvastext`, `linktext`, `accentcolor`, etc.) | ✅ | 2 | Task 10 cycle 1 review pass adds [`CssSystemColors`](../src/NetPdf.Css/ComputedValues/PropertyResolvers/CssSystemColors.cs) — fixed-value print palette per CSS Color 4 §10. Required so the cascade's default for `color` (which is `canvastext` per spec) parses cleanly. Screen-browser color-scheme switching is post-v1; print uses paper white / ink black + Mosaic-era link colors. |
| `color` (`currentcolor` keyword) | ✅ | 2 | Resolved to a dedicated [`ComputedSlotTag.CurrentColor`](../src/NetPdf.Css/ComputedValues/ComputedSlot.cs) tag (no payload), so no user-authored color value can collide with the sentinel. The paint stage substitutes the cascaded `color` value when it sees this tag. |
| `color` (lab, lch, hwb) | 🧪 | 2 | AngleSharp.Css parses these natively. Task 10 cycle 1 doesn't yet wire the typed evaluation — `ColorResolver` emits `CSS-PROPERTY-VALUE-INVALID-001` for `lab()` / `lch()` until cycle 2 adds the L4 §4.3 / §4.4 conversion. |
| `color` (oklab, oklch) | 🧪 | 2 | AngleSharp.Css 1.0.0-beta.144 SILENTLY CORRUPTS `oklch(...)` to bogus rgba. Task 3's pre-pass preserves the authored text in `CssDeclaration.Value.RawText`; Task 10 cycle 1 emits `CSS-PROPERTY-VALUE-INVALID-001` for the modern color spaces. Cycle 2 (paired with the pre-pass capture) will compute typed values per CSS Color 4 §6 (Oklab) and §7 (Oklch). |
| `color-mix()` | 🧪 | 2 | AngleSharp.Css 1.0.0-beta.144 drops the declaration entirely (empty rule body). Task 3's pre-pass restores the authored value as raw text; Task 10 cycle 1 emits `CSS-PROPERTY-VALUE-INVALID-001`. Cycle 2 will compute the mix per CSS Color 5 §2. |
| `light-dark()` | 🧪 | 2 | Same AngleSharp drop as `color-mix()`. Raw text preserved by Task 3's pre-pass; Task 10 cycle 1 emits `CSS-PROPERTY-VALUE-INVALID-001`. Cycle 2 will evaluate against `PreferredColorScheme`. |
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
| Custom properties (`--*`, `var()`) | ✅ | 2 | Substitution implements CSS Custom Properties L1 §3.5: lookup against the cascaded custom-property table (with parent-chain inheritance), recursive expansion, fallback (incl. empty fallback `var(--x,)` → empty string), Tarjan-SCC cycle invalidation (every cycle member is "invalid at computed value time" so external refs use the var()'s fallback), depth + output-length safety limits (32 frames / 1 MiB) emitting `CSS-VAR-EXPANSION-LIMIT-001` (cycles use the distinct `CSS-VAR-CIRCULAR-001`). **v1 known gap:** `var()`-bearing shorthand declarations rely on AngleSharp.Css's parse-time longhand expansion, which works for the common case where each longhand gets a distinct portion of the shorthand value (e.g., `padding: var(--w) var(--w) var(--w) var(--c)` splits cleanly per longhand) but is incorrect for opaque-substitution cases like `border: var(--bundle)` where the var() resolves to multiple tokens that need re-parsing. CSS Custom Properties L1 §3 calls these "pending substitution values" and requires shorthand expansion to happen AFTER var() resolution. Tasks 9–10 typed-value parsers will close this gap when they re-expand shorthands. |
| `calc()` / `min()` / `max()` / `clamp()` / `abs()` / `sign()` | 🧪 | 2 | Recursive-descent reducer per CSS Values L4 §10. Reduces fully when all operands share a single dimension class (or are unitless `Number`). Spec-correct subset (Task 9 + cycle 1): operator precedence + parentheses; ASCII-CI function names; nested math (e.g., `calc(min(8px, 4px) + 2px)`); same-unit dimension division yields a unitless `Number` per §10.10 (so `calc(16px / 2px) = 8`); `+` and `-` require whitespace on both sides per §10.4. **v1 deferred** (preserved verbatim, no diagnostic — layout/typed-value pipeline finalizes): font-relative units (`em`, `rem`, `ch`, `ex`, `lh`, `rlh`, `cap`, `ic`), viewport-relative (`vw`/`vh`/`svw`/`lvw`/`dvw`/`vmin`/`vmax`/etc.), container-relative (`cqw`/`cqh`/`cqi`/`cqb`/`cqmin`/`cqmax`), and any expression mixing percentage with another dimension class. **v1 invalid** (preserved + `CSS-CALC-INVALID-001`): `dim * dim` (no area unit in CSS), cross-class division (e.g., length / angle), missing whitespace around `+`/`-`, malformed input. Division by zero emits `CSS-CALC-DIV-BY-ZERO-001`. Pipeline order: `var()` substitution → calc reduction (verified by [VarToCalcPipelineTests](../tests/NetPdf.UnitTests/Css/Cascade/VarToCalcPipelineTests.cs)). Trigonometric functions (`sin`/`cos`/`tan`), exponential (`pow`/`sqrt`/`hypot`/`log`/`exp`), `mod`/`rem`/`round`, and stepped-value functions are **post-v1**. |
| CSS Nesting (`& { ... }`) | ✅ | 2 | |
| `@layer` cascade layers — block-form parsing | 🧪 | 2 | AngleSharp.Css drops `@layer name { ... }` entirely. Task 3's pre-pass captures it as a `CssAtRule { Name = "layer", RawBody = "..." }`. **v1 known gap:** the cascade does not yet re-parse `RawBody` into nested style rules, so block-form `@layer` bodies emit `CSS-AT-RULE-UNKNOWN-001` and don't apply. The cascade infrastructure for layer ordering IS wired (`LayerRegistry` + `CascadeKey.LayerOrder` honor §6.4.4 normal-vs-important reversal), so when the body is decomposed (synthetic AST today, future Task 3 enhancement for real CSS), layer precedence resolves correctly. |
| `@layer` statement-form (`@layer one, two;`) | 🧪 | 2 | Same as block-form parsing path. Statement-form preludes ARE applied today: the cascade registers the layer names in declaration order so subsequent block-form rules pick up the assigned indices. Block-form bodies remain a known v1 gap (see above). |
| `:has()` selector — parsing | ✅ | 2 | Selector compiles. |
| `:has()` selector — rendering | 📥 | post-v1 | Currently treated as no-match; emits `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001`. Roadmap v1.4. |
| `:is()`, `:where()`, `:not()` | ✅ | 2 | |
| Container queries (`@container`) — parsing | 🧪 | 2 | AngleSharp.Css drops `@container ... { ... }` entirely. Task 3's pre-pass captures it as a `CssAtRule { Name = "container", RawBody = "..." }`. |
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

Last updated: 2026-05-05 (Phase 2 Task 8 review cycle 1 — `var()` substitution row expanded with implementation details + known gap on var()-bearing shorthands. Cycle invalidation, empty-fallback semantics, depth + output safety limits all wired and tested.).
