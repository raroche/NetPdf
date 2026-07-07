# NetPdf v1 Compatibility Matrix

This document is the authoritative answer to "does NetPdf support feature X?" Updated as features ship.

**Legend:**
- ✅ Supported — fully implemented and tested.
- 🧪 Partial — implemented with documented caveats (see notes column).
- 📥 Parsed only — CSS grammar accepted; rendering not yet implemented; emits a structured diagnostic with stable code.
- ❌ Out of scope for v1 — not parsed or not rendered; emits diagnostic.

---

## HTML

| Feature | Status | Notes |
|---|---|---|
| HTML5 parsing | ✅ | Via AngleSharp; quirks-mode supported. |
| `<script>` execution | ❌ | Collected and emitted as `HTML-SCRIPT-IGNORED-001`. |
| `<style>` & inline `style=""` | ✅ | |
| `<img>` | 🧪 | A BLOCK-level `<img>` (`display: block`) renders end-to-end — `data:` URIs decode inline with NO loader (the self-contained default; `file:`/`http(s):` via `HtmlPdfOptions.ResourceLoader` under `SecurityPolicy`), PNG/JPEG native passthrough + GIF/WebP/BMP via the SkiaSharp raster fallback (AVIF platform-dependent — macOS SkiaSharp 3.119 lacks libavif), CSS 2.2 §10.3.2 sizing (CSS `width`/`height` > HTML dimension attributes > intrinsic 1:1 px, aspect-ratio completion from an absolute side), `margin: auto` centering, per-URI + content-hash XObject dedup. `object-fit` fits the content in its content box (CSS Images 3 §5.5 — `fill`/`contain`/`cover`/`none`/`scale-down`, positioned per `object-position` — per-axis keywords / absolute lengths / percentages, the 50% 50% initial when unset (CSS Images 3 §5.6; rendered from the RAW cascade winner — the property is NOT yet registered, so `@supports (object-position: …)` does not report it and registration is deferred, like `border-radius`) — overflow clipped). INLINE `<img>` stays the atomic-inline deferral (`LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001`); `object-position` registration (a `<position>` metadata type) + edge-offset (4-value) forms, `%` dimension attributes, `srcset`, ICC profiles deferred ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `<svg>` inline | ✅ | Static subset; see SVG section. |
| `<a>` hyperlinks | ✅ | Emitted as PDF `Link` annotations. |
| `<table>` / `<thead>` / `<tbody>` / `<tfoot>` | ✅ | `<thead>`/`<tfoot>` repeat across pages when `display: table-header-group`/`-footer-group`. |
| `<form>` widgets | ❌ | Tagged for future AcroForm support. |
| `<video>` / `<audio>` | ❌ | Out of scope — emits `HTML-MEDIA-UNSUPPORTED-001`. |
| `<iframe>` | ❌ | Out of scope — emits `HTML-IFRAME-UNSUPPORTED-001`. |

---

## CSS — Layout

| Feature | Status | Notes |
|---|---|---|
| Block layout (margins, padding, borders, sizing) | ✅ | Including margin collapsing, BFC. |
| Inline layout (line boxes, vertical-align, line-height, white-space) | ✅ | |
| Floats (`float`, `clear`) | ✅ | |
| `position: static` / `relative` / `absolute` | ✅ | |
| `position: fixed` | ✅ | Repeated on every page. |
| `position: sticky` | ❌ | Emits `CSS-POSITION-STICKY-UNSUPPORTED-001`. |
| Tables (auto + fixed layout, border-collapse, span) | ✅ | |
| Multi-column (`column-count`, `column-width`) | 🧪 | Partial / approximated. `column-count` is supported (equal-column split, serial fill, forced-overflow diagnostic). `column-width` is parsed + cascaded but unused at layout time. Column balancing (`column-fill: balance`), `column-span: all`, painted column rules, multi-page multicol (the multicol container fragmenting across pages) all deferred. See [docs/deferrals.md#multicol-balancing-pagination](deferrals.md#multicol-balancing-pagination). |
| Flexbox (CSS Flexible Box Layout L1) | ✅ | L1 spec incl. the `flex` shorthand (§7.4), `gap`/`column-gap`/`row-gap` gutters (CSS Box Alignment L3 §8 — consume free space before grow/shrink + justify-content, percentages resolve against the content box), containers honoring their own explicit `width` + `margin: 0 auto` centering, **`align-items`/`align-self: baseline`** (row cross-axis first-baseline alignment), and **`flex-basis: content`/`max-content`/`min-content`** intrinsic sizing on the nowrap row main axis. Known gaps: intrinsic `flex-basis` on WRAP rows + `fit-content`, `align-content: baseline`, RTL column cross-axis ([`flex-layouter-features`](deferrals.md#flex-layouter-features)). |
| CSS Grid (Level 1) | ✅ | Track sizing with `auto`/`fr`/`minmax`/`fit-content`/`repeat`/`auto-fill`/`auto-fit`; sparse + dense auto-placement; `grid-template-areas`; `column-gap`/`row-gap`/`gap` gutters between tracks (incl. spanned items + auto-height extent); container `margin: 0 auto` centering. Known gaps: a `%` gutter is treated as 0 ([`gap-percentage-sizing`](deferrals.md#gap-percentage-sizing)); `fr` tracks don't subtract gutters from their free space ([`grid-gap-fr-track-sizing`](deferrals.md#grid-gap-fr-track-sizing)). |
| CSS Grid Level 2 (subgrid) | ❌ | Parsed only; emits `CSS-SUBGRID-UNSUPPORTED-001`. Roadmap v1.3. |

---

## CSS — Paged Media

| Feature | Status | Notes |
|---|---|---|
| `@page` size, margin | ✅ | Named/orientation/`<length>` size + per-side margins; `!important`, paper-size-`@media` ignore (§3.3), `%` margins vs. the resolved page box. |
| `@page :first` / `:left` / `:right` / `:blank` | 🧪 | `@page :first` is APPLIED — on the single (first) page it overrides the bare `@page` (margin / size / margin boxes) by cascade specificity, via `AtPageRules.EnumeratePageRules` yielding bare-then-`:first` (a bare `!important` still beats a `:first` normal). `:left` / `:right` / `:blank` + named pages are recognized but DEFERRED (they need the multi-page driver's page context) ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| Page-margin boxes (`@top-left`, `@top-center`, `@top-right`, `@bottom-*`, `@left-*`, `@right-*`) | 🧪 | All 16 boxes: literal + `attr()` + `counter(page)`/`counter(pages)` (incl. `<counter-style>` — decimal / decimal-leading-zero / lower+upper-roman / lower+upper-alpha+latin / lower-greek; an unknown / unimplemented style falls back to decimal per CSS Counter Styles §7.1.4) content; per-box style (`font`/`font-*` incl. relative sizes / `color` / `text-align` / `vertical-align` / `background-color` / `border` + per-side `border-<side>` + the `border-width`/`-style`/`-color` box shorthands / `padding` + the border content-inset) + page-context→root inheritance + `@page :first` + §5.3 box sizing (shrink-to-fit + explicit `width`/`height` honoring `box-sizing: content-box\|border-box` — absolute, `%`-of-band, font-/viewport-relative `em`/`ex`/`ch`/`rem`/`vw`/`vh`/`vmin`/`vmax`, and `calc()`/`min()`/`max()`/`clamp()`/`round()`/`mod()`/`rem()`/`abs()`/`sign()` sizes (CSS Values §10 sum/product + §10.2 comparison + §10.6 stepped + §10.7 sign functions; usable standalone too) — + sibling-overlap distribution with min/max-content flex + content overflow wrapping with per-line alignment; vertical left/right + corner boxes WRAP at their fixed band width; `white-space` cascaded + inherited — a declared `nowrap`/`pre` keeps a rigid single line; padding takes `%`-of-band / font-/viewport-relative / `calc()` values too) + `string(name)`/`string-set` (incl. the `content()` form — the canonical running header) + `element(name)` (via `position: running()`) content. A STANDALONE `element()` also renders the running element's OWN `background-color` + `border-*` + `padding-*` + `text-align` (cascaded under the box's own declarations, the box's own winning — full-block first cut), STACKS the running element's BLOCK-level children as separate lines — RECURSING into nested blocks (one line per nested block, 16 levels deep, budget-bounded), and CLIPS overflow (surfaced via `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001`): height overflow at line granularity (the first N whole lines that fit paint; 0 fit → decoration-only box), width overflow (an unbreakable run wider than the box) at GLYPH level via a PDF `W n` clip path on the padding box — an explicit `overflow: visible` on the box opts out (content spills, no diagnostic; clip-by-default inverts the spec-initial `visible`, a documented approximation). A `border-radius` rounds the background band PER-CORNER (absolute or `%`-ellipse), the uniform border (the same filled ring as the body), and the background-image clip — at parity with the body (the shorthand expands via `BorderRadiusShorthandExpander` + the corner longhands cascade through `MarginBoxStyle`; the `Rx / Ry` slash + relative radii defer to square); a co-declared standalone `element()` paints its own decoration as a NESTED band at its content block. Sizes/padding also take the §10.8 trig + §10.9 exponential functions (`hypot(30px, 40px)`, `calc(100px * cos(0))`). A standalone `element()`'s stacked lines render in each LEAF block's OWN font + colour, with the leaf's own background/border as a per-LINE band, its own `text-align` per line (the box's wins), and TRUE per-line pitch. A leaf block's own horizontal MARGINS inset its line's band + glyphs/extent per line (absolute, clamped ≥ 0; padding stays inside the band, margins outside). A DECORATED intermediate container's own band spans its descendants' lines (pre-order nesting, vertical margins fold into boundary gaps, its h-margins inset its band). A margin box's `background-image: url(...)` tiles over its band (repeat / position / size / origin / clip; `PrintBackgrounds`-gated incl. the prefetch). A container's horizontal margin+padding INSET its descendants' lines and nested bands, and its VERTICAL padding + §4.3-gated borders extend its band over the padding strip + ADD to (not max-collapse) the boundary gap. Non-page `counter()` / `counters()` content, cross-page running, container WIDTH sub-box wrap and container-relative units stay deferred ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `string()` / `string-set`, `element()` / `position: running()`, named pages | 🧪 | FIRST CUT (single page): `string(name [, first \| last])` resolves a `string-set` (literal + `attr()` + `content()` content-lists, one-or-more comma-separated pairs); `content()` is the element's own text, GCPM-normalized as if `white-space: normal` (recovered from AngleSharp via `CssPreprocessor`); `string(name)` defaults to `first` (the first assignment on the page) per GCPM §7.3, `last` is the exit value; `element(name [, first \| last])` resolves a `position: running()` element's text (removed from normal flow, GCPM-normalized as `white-space: normal`), defaulting to `first` per GCPM §7.4, and a STANDALONE `element(name)` renders the text in the element's OWN font + color PLUS its own `background-color` + `border-*` + `padding-*` decoration + `text-align` (cascaded under the box's own declarations, the box's own winning — first cut of full block rendering). `content(before\|after)` resolves the host's pseudo content in `string-set` (missing pseudo → empty). DEFERRED: `content(first-letter\|marker)`, `string`/`element(name, start \| first-except)` (cross-page), the running element's REAL nested BLOCK LAYOUT (its BLOCK children STACK as separate lines, RECURSE 16 deep, and each line renders in its leaf block's OWN font/colour with its own per-LINE decoration band, `text-align` (the box's winning), pitch (its own `line-height` — absolute/unitless/em), collapsed vertical-margin inter-line gaps, vertical per-line PADDING growing its band/pitch, and HORIZONTAL per-line padding insetting its line's glyphs + alignment extent (the wrap width isn't narrowed per segment); a CO-DECLARED box/element nest as separate bands (a box property no longer suppresses the element's); relative units/`inherit` in its style approximate against the page context; the element's own `%`/`em`/`calc()` padding resolves like the box's; vertical-edge height overflow is CLIPPED at line granularity + width overflow at glyph level via a PDF clip path, surfaced via `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001` — an explicit `overflow: visible` opts out), cross-page "running" persistence, and named-page selectors (multi-page-gated) ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `break-before`, `break-after`, `break-inside` | ✅ | |
| `widows`, `orphans` | ✅ | Honored by the pagination optimizer's cost model. |
| `<thead>` / `<tfoot>` repetition | ✅ | |

---

## CSS — Typography

| Feature | Status | Notes |
|---|---|---|
| `font-family`, `font-size`, `font-weight`, `font-style`, `font-stretch` | ✅ | |
| `@font-face` with TTF, OTF, WOFF, **WOFF2** | 🧪 | All four formats are **parsed**; WOFF2 decompressed via `System.IO.Compression.BrotliStream` (built into .NET, no extra dep). **Embedding scope:** TTF only. OTF/CFF embedding (`FontFile3` / `CIDFontType0C`) is deferred — `EmbeddedTtfFont.Build` throws explicitly on CFF-flavored fonts rather than producing malformed PDF. |
| Web font fetching via `IResourceLoader` | ✅ | |
| Font fallback chain | ✅ | |
| OpenType ligatures, kerning | ✅ | Via HarfBuzz. |
| Bidi (RTL/LTR mixed text) | ✅ | UAX #9. |
| Complex scripts (Indic, Arabic, Hebrew, CJK, Thai) | ✅ | Via HarfBuzz; quality varies by script — known limitations documented. |
| Hyphenation (`hyphens: auto`) | 🧪 | Liang patterns. **en-US is bundled** (4,938 patterns + 14 exceptions). Other languages ship as optional `NetPdf.Languages.*` NuGet packs (Cjk, Indic, European, Arabic, plus an `All` meta-package). |
| `text-align`, `text-decoration`, `text-transform`, `letter-spacing`, `word-spacing` | ✅ | |
| `writing-mode` (vertical) | 🧪 | `vertical-rl`/`vertical-lr` supported; sideways modes not. |

---

## CSS — Visual

| Feature | Status | Notes |
|---|---|---|
| `color` (named, hex, rgb, hsl) | ✅ | The [`ColorResolver`](../src/NetPdf.Css/ComputedValues/PropertyResolvers/ColorResolver.cs) computes typed values from named (147 entries via [`CssNamedColors`](../src/NetPdf.Css/ComputedValues/PropertyResolvers/CssNamedColors.cs)), every hex form (`#rgb`/`#rgba`/`#rrggbb`/`#rrggbbaa`), `rgb()`/`rgba()` and `hsl()`/`hsla()` in BOTH legacy comma + modern whitespace + slash-alpha syntax. Strict syntax separation per CSS Color 4 §4.2 — mixed comma + slash forms emit `CSS-PROPERTY-VALUE-INVALID-001`. |
| `color` (system colors — `canvas`, `canvastext`, `linktext`, `accentcolor`, etc.) | ✅ | The [`CssSystemColors`](../src/NetPdf.Css/ComputedValues/PropertyResolvers/CssSystemColors.cs) table provides a fixed-value print palette per CSS Color 4 §10. Required so the cascade's default for `color` (which is `canvastext` per spec) parses cleanly. Screen-browser color-scheme switching is post-v1; print uses paper white / ink black + Mosaic-era link colors. |
| `color` (`currentcolor` keyword) | ✅ | Resolved to a dedicated [`ComputedSlotTag.CurrentColor`](../src/NetPdf.Css/ComputedValues/ComputedSlot.cs) tag (no payload), so no user-authored color value can collide with the sentinel. The paint stage substitutes the cascaded `color` value when it sees this tag. |
| `color` (lab, lch, hwb) | 🧪 | AngleSharp.Css parses these natively. The typed evaluation is not yet wired — `ColorResolver` emits `CSS-PROPERTY-VALUE-INVALID-001` for `lab()` / `lch()` (the L4 §4.3 / §4.4 conversion is not yet implemented). |
| `color` (oklab, oklch) | 🧪 | AngleSharp.Css 1.0.0-beta.144 SILENTLY CORRUPTS `oklch(...)` to bogus rgba. The pre-pass preserves the authored text in `CssDeclaration.Value.RawText`; `CSS-PROPERTY-VALUE-INVALID-001` is emitted for the modern color spaces. Typed-value computation per CSS Color 4 §6 (Oklab) and §7 (Oklch) is not yet implemented. |
| `color-mix()` | 🧪 | AngleSharp.Css 1.0.0-beta.144 drops the declaration entirely (empty rule body). The pre-pass restores the authored value as raw text; `CSS-PROPERTY-VALUE-INVALID-001` is emitted. Computing the mix per CSS Color 5 §2 is not yet implemented. |
| `light-dark()` | 🧪 | Same AngleSharp drop as `color-mix()`. Raw text preserved by the pre-pass; `CSS-PROPERTY-VALUE-INVALID-001` is emitted. Evaluation against `PreferredColorScheme` is not yet implemented. |
| `background-color` | ✅ | |
| `background-image: url(...)` | 🧪 | A single `url(...)` on a BODY block — or a PAGE MARGIN BOX — tiles over the `background-origin` box (a BODY block honors `border-box` / `padding-box` [initial] / `content-box`; a margin box uses its band), gated by `PrintBackgrounds` (incl. the prefetch — no fetch when off), over the `background-color`, under borders/text; partial edge tiles clip at the `background-clip` box (border-box initial). `background-repeat`/`-size`/`-position` drive the tiling on BOTH (see the row below). Tilings above the 16-tile per-box loop threshold emit ONE PDF tiling-pattern fill (ISO 32000-2 §8.7.3 — O(1) for any count; the old 4096-tile cap is gone). BOTH a BODY block and a page-MARGIN box honor `background-origin` / `-clip` (see the row below; the margin box reuses the body inset helper on its own style). Approximation: rectangular tiles over a rounded margin-box band. **Multi-layer (comma-separated) `background-image` lists are supported on a BODY / element box** (NOT page margin boxes — those take a single `url(...)`, else `CSS-BACKGROUND-IMAGE-UNSUPPORTED-001`) — each layer (url / linear / radial / conic, mixed) paints back-to-front. A URL layer honors all of its `background-position`/`-size`/`-repeat`/`-origin`/`-clip` (cycled from the comma lists); a GRADIENT layer honors `-origin`/`-clip` but `-size`/`-position`/`-repeat` are deferred (the shading fills the origin box) and surface `CSS-BACKGROUND-IMAGE-UNSUPPORTED-001` once. An unrecognized form / unparseable layer surfaces the same code. |
| `background-image: linear-gradient()` | ✅ | PDF native axial shading. Supports `repeating-`, `<angle>`/`to <side\|corner>`, %/length stop positions, double-position stops (§3.4) + color-interpolation hints (§3.4.2, the exponential easing curve sampled as stops — an exact stop pinned at the hint, linear between samples). Stop colors interpolate in premultiplied RGBA (§3.4.2). A translucent stop (per-stop alpha) falls back to a Skia raster (image + `/SMask`) since a native shading is DeviceRGB; opaque stays native. A translucent gradient too large to raster is SKIPPED (`CSS-GRADIENT-ALPHA-UNSUPPORTED-001`), never dropped to an opaque shading. Honors `background-origin`/`-clip` AND `background-size`/`-position`/`-repeat` (single + multi-layer) — a non-initial size/position/repeat TILES the shading (the gradient is sized + positioned + repeated, incl. repeat-x/-y/space/round). An unsupported VALUE (e.g. `em`) or a grid beyond the tile cap is diagnosed (`CSS-BACKGROUND-IMAGE-UNSUPPORTED-001`) and falls back. |
| `background-image: radial-gradient()` | ✅ | PDF native radial shading (ellipse via CTM). Same stop features as linear (double-position, hints incl. premultiplied interpolation, %/length); a translucent stop falls back to a Skia raster (image + `/SMask`); opaque stays native; an over-cap translucent gradient is SKIPPED (`CSS-GRADIENT-ALPHA-UNSUPPORTED-001`), not opaque-approximated. Honors `background-origin`/`-clip` AND `-size`/`-position`/`-repeat` (tiled); unsupported value / over-cap diagnosed. |
| `background-image: conic-gradient()` | 🧪 | Skia raster fallback (per-stop alpha + double-position + hints supported via the shared stop pipeline). Honors `background-origin`/`-clip` AND `-size`/`-position`/`-repeat` (the sweep raster tiles per tile); unsupported value / over-cap diagnosed. |
| Multiple backgrounds | ✅ | |
| `background-size`, `-position`, `-repeat`, `-clip`, `-origin` | 🧪 | First cut (body blocks): `background-repeat` (the 4 single keywords + the two-value axis form — a repeating axis covers the area at the position's phase, a non-repeating axis paints one clipped tile; `space` distributes floor(area/tile) tiles with equal gaps, `round` rescales the tile to fit — both ride the per-tile loop and, above the 16-tile threshold, the PDF tiling pattern's `/XStep`//`/YStep`); `background-size` (`auto`/`contain`/`cover`/`<length\|%>{1,2}` — % against the area, aspect completion from the intrinsic ratio); `background-position` (per-axis keywords incl. the swapped pair, absolute lengths, percentages per the CSS B&B §3.6 rule; one value → other axis centers; the 3-/4-value edge-offset form `left 10px top 5px` — an offset FROM a named edge — parses too, facade-reachable via MARGIN-BOX raws, a BODY / `object-position` 4-value form being AngleSharp-dropped). An unsupported form falls back to its initial WHOLE via `CSS-BACKGROUND-IMAGE-UNSUPPORTED-001` once per render. `background-origin` (the positioning area — `border-box` / `padding-box` [initial] / `content-box`) + `background-clip` (the paint/clip rect — `border-box` [initial] / `padding-box` / `content-box`) drive a BODY block's tiling AND a page-MARGIN box's, the border box inset by the used border / border+padding (the margin box reuses the body inset helper on its own style). `background-attachment` (`scroll`[initial]/`fixed`/`local`) is registered for VALIDATION only (`@supports` reports it + an invalid value is diagnosed) — rendering does NOT consume the value yet (parse-only metadata): paged media has no scroll, so every value paints element-relative, `fixed` page-relative positioning being a documented deferral (silently approximated, not diagnosed). The margin-box `background-origin`/`-clip` reads flow through the box's cascade (importance + CSS-wide + invalid-value diagnostics). AngleSharp-beta splits repeat/position into `-x`/`-y` longhands; the capture recomposes them. |
| `border`, `border-style` (all variants) | ✅ | |
| `border-radius` | 🧪 | BODY mostly complete; margin box first-cut. A BODY block's background COLOR band rounds with PER-CORNER radii — the `border-radius` 1–4-value shorthand + the four registered corner longhands, each an absolute length (circular) or a PERCENTAGE that resolves against the box width (horizontal) / height (vertical), so a non-square `50%` box is an ELLIPSE (CSS B&B 3 §4.1) — via the per-corner elliptical `PdfPage.FillRoundedRectangle(CornerRadii)` (§4.2 overlap-scaling). A uniform border (same style/width/colour on all edges) + a radius paints ONE filled RING (`PdfPage.FillRoundedRectangleRing` — the even-odd annulus between the border box and the padding box) instead of the four square edge rects: a fill, so the outer corner is exact for any border width (a small radius under a thick border keeps its rounding) and a semi-transparent border composites correctly (`/ca`); a radius also rounds the background-IMAGE clip (`PdfPage.BeginRoundedRectangleClip`, both the per-tile loop and tiling-pattern paths). A page-MARGIN box now has PARITY: its band rounds per-corner/`%` (the `border-radius` shorthand expands via `BorderRadiusShorthandExpander`, since margin-box bodies bypass AngleSharp; the corner longhands cascade through `MarginBoxStyle`), its uniform border rounds (the same ring, free via `FragmentPainter.PaintBorders`), and its background-image clip rounds. DEFERRED: the explicit two-radii `Rx / Ry` elliptical slash spelling (an AngleSharp drop / margin-box defer → square); rounded NON-uniform borders; font-/viewport-relative margin-box radii (deferred → square) ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `border-image` | 🧪 | Decoded and 9-sliced; complex outsets may differ from Chrome. |
| `box-shadow` (sharp) | ✅ | Native PDF emit. |
| `box-shadow` (blurred) | 🧪 | Skia raster fallback. |
| `text-shadow` (sharp / blurred) | 🧪 | A SHARP shadow (blur = 0) is the run's glyphs offset in the shadow color, painted under the text. A BLURRED shadow rasterizes the run's glyph OUTLINES via Skia (2×, Gaussian σ = blur/2) → an image XObject with an alpha `/SMask` (`CSS-TEXTSHADOW-BLUR-RASTER-001`). Multiple comma-separated layers paint back-to-front (first-listed on top), mixing sharp + blurred. `text-shadow` INHERITS to descendant text. `em`/`rem`/`%` offsets/blur are rejected (`CSS-TEXTSHADOW-UNSUPPORTED-001`). DEFERRED: an over-cap blurred run (or a font Skia can't read) falls back to a sharp offset; a blurred shadow is rasterized PER glyph segment, so a justified line's blurred words don't bleed across the inter-word gaps ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `outline` | 🧪 | First cut (CSS UI 4 §5): `outline-width` / `outline-style` / `outline-color` + the `outline` shorthand (AngleSharp expands it) + `outline-offset` are registered (`@supports` reports them); `outline-offset` is recovered from an AngleSharp-beta drop via `CssPreprocessor`. The outline paints as a filled ring OUTSIDE the border box (it does NOT affect layout) via the shared `PdfPage.FillRoundedRectangleRing` — the annulus between the border box grown by `outline-offset` (inner) and again by `outline-width` (outer), in `outline-color` (initial currentcolor). `outline-style` admits the border-style line styles EXCEPT `hidden` (invalid for an outline, CSS UI §5.2) PLUS `auto`; `outline-color: auto` (the CSS UI 4 initial — `invert` is retired) is accepted, approximated as currentcolor; `outline-width` is a non-negative `<line-width>` (a negative value falls back to `medium`). A `border-radius` rounds the outline to follow the box (corner radii grown by the gap to each outline edge, a sharp box corner staying sharp — §5.3). A negative `outline-offset` pulls it inward (clamped per axis to keep the collapsed outline centred). `dotted`/`dashed`/`double` outlines render faithfully (sharp + rounded — stroked centreline / two concentric rings, shared `PaintStyledRing`). DEFERRED: only the 3D `outline-style` (groove/ridge/inset/outset) paints as solid + diagnosed `PAINT-BORDER-STYLE-APPROXIMATED-001` (per-side bevel can't follow the ring, once per conversion, shared with borders); `outline-color: auto`'s true UA colour ([deferrals.md](deferrals.md#layout-to-pdf-pipeline)). |
| `opacity` | 🧪 | Registered; applied via a PDF ExtGState carrying `/ca` (fill) + `/CA` (stroke) that wraps the element's decoration + image, with the same alpha folded into its glyph fill. `<number>` + `<percentage>` (the % form recovered from an AngleSharp-beta drop). `opacity: 1` is a no-op (byte-identical). DEFERRED: this is a PER-OBJECT alpha, not an isolated transparency GROUP — a self-overlapping element composites slightly differently than group opacity (`CSS-OPACITY-GROUP-APPROXIMATED-001`; the faithful group needs a transparency-group Form XObject — the IPaintTarget epic). |
| `mix-blend-mode` | ✅ | PDF ExtGState `/BM`. |
| `clip-path: rect()` / `inset()` / `polygon()` | ✅ | Native PDF clipping; `inset()` round uses per-corner radii. |
| `clip-path: path()` | ✅ | Native PDF clip (SVG path → PDF path + `W n`/`W* n`, curves preserved, fill-rule honored). |
| `mask`, `mask-image` | 🧪 | Skia raster fallback. |
| `filter: blur` / `drop-shadow` / `brightness` / `contrast` / `saturate` / `sepia` / `hue-rotate` / `invert` / `grayscale` | 🧪 | Skia raster fallback per filtered subtree. |
| `transform` (2D) | ✅ | Translate, rotate, scale, skew, matrix. `em`/`rem`/`%` translate offsets + `transform-origin` lengths resolve (% against the box border-box). |
| `transform` (3D) | ❌ | Emits `CSS-TRANSFORM-3D-UNSUPPORTED-001`. |
| Animations / transitions | ❌ | PDF is static. Emits `CSS-ANIMATION-UNSUPPORTED-001`. |

---

## CSS — Modern syntax

| Feature | Status | Notes |
|---|---|---|
| Custom properties (`--*`, `var()`) | ✅ | Substitution implements CSS Custom Properties L1 §3.5: lookup against the cascaded custom-property table (with parent-chain inheritance), recursive expansion, fallback (incl. empty fallback `var(--x,)` → empty string), Tarjan-SCC cycle invalidation (every cycle member is "invalid at computed value time" so external refs use the var()'s fallback), depth + output-length safety limits (32 frames / 1 MiB) emitting `CSS-VAR-EXPANSION-LIMIT-001` (cycles use the distinct `CSS-VAR-CIRCULAR-001`). `var()` inside a `background` / `border[-side]` shorthand is supported — including an opaque whole-value `border: var(--bundle)` that resolves to multiple tokens: the shorthand is recovered and re-expanded to its longhands **after** `var()` substitution (CSS Custom Properties L1 §3 "pending substitution values"), so such declarations render correctly rather than being dropped. |
| `calc()` / `min()` / `max()` / `clamp()` / `round()` / `mod()` / `rem()` / `abs()` / `sign()` / trig / exponential | 🧪 | Recursive-descent reducer per CSS Values L4 §10. Reduces fully when all operands share a single dimension class (or are unitless `Number`). Spec-correct subset: operator precedence + parentheses; ASCII-CI function names; nested math (e.g., `calc(min(8px, 4px) + 2px)`); same-unit dimension division yields a unitless `Number` per §10.10 (so `calc(16px / 2px) = 8`); `+` and `-` require whitespace on both sides per §10.4. **v1 deferred** (preserved verbatim, no diagnostic — layout/typed-value pipeline finalizes): font-relative units (`em`, `rem`, `ch`, `ex`, `lh`, `rlh`, `cap`, `ic`), viewport-relative (`vw`/`vh`/`svw`/`lvw`/`dvw`/`vmin`/`vmax`/etc.), container-relative (`cqw`/`cqh`/`cqi`/`cqb`/`cqmin`/`cqmax`), and any expression mixing percentage with another dimension class. **v1 invalid** (preserved + `CSS-CALC-INVALID-001`): `dim * dim` (no area unit in CSS), cross-class division (e.g., length / angle), missing whitespace around `+`/`-`, malformed input. Division by zero emits `CSS-CALC-DIV-BY-ZERO-001`. Pipeline order: `var()` substitution → calc reduction (verified by [VarToCalcPipelineTests](../tests/NetPdf.UnitTests/Css/Cascade/VarToCalcPipelineTests.cs)). The LENGTH-property path (the shared `CalcLengthEvaluator`: body cascade folding + the post-build relative pass + margin-box sizes) additionally evaluates the §10.6 stepped (`round()`/`mod()`/`rem()`), §10.7 sign (`abs()`/`sign()`), §10.8 trigonometric (`sin`/`cos`/`tan`/`asin`/`acos`/`atan`/`atan2`, with `deg`/`grad`/`rad`/`turn` angles + the `e`/`pi` constants), and §10.9 exponential (`pow`/`sqrt`/`hypot`/`log`/`exp`) functions; a body math function with font-/viewport-relative terms resolves at the post-build pass, a PERCENTAGE term stays diagnosed (layout-time containing block — deferrals.md). The cascade var()→calc reducer itself keeps the §10.4 sum/product subset. |
| CSS Nesting (`& { ... }`) | ✅ | |
| `@layer` cascade layers — block-form parsing | 🧪 | AngleSharp.Css drops `@layer name { ... }` entirely. The pre-pass captures it as a `CssAtRule { Name = "layer", RawBody = "..." }`. **v1 known gap:** the cascade does not yet re-parse `RawBody` into nested style rules, so block-form `@layer` bodies emit `CSS-AT-RULE-UNKNOWN-001` and don't apply. The cascade infrastructure for layer ordering IS wired (`LayerRegistry` + `CascadeKey.LayerOrder` honor §6.4.4 normal-vs-important reversal), so when the body is decomposed (synthetic AST today, a future enhancement for real CSS), layer precedence resolves correctly. |
| `@layer` statement-form (`@layer one, two;`) | 🧪 | Same as block-form parsing path. Statement-form preludes ARE applied today: the cascade registers the layer names in declaration order so subsequent block-form rules pick up the assigned indices. Block-form bodies remain a known v1 gap (see above). |
| `:has()` selector — parsing | ✅ | Selector compiles. |
| `:has()` selector — rendering | 📥 | Currently treated as no-match; emits `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001`. Roadmap v1.4. |
| `:is()`, `:where()`, `:not()` | ✅ | |
| Container queries (`@container`) — parsing | 🧪 | AngleSharp.Css drops `@container ... { ... }` entirely. The pre-pass captures it as a `CssAtRule { Name = "container", RawBody = "..." }`. |
| Container queries — rendering | 📥 | Emits `CSS-CONTAINER-QUERY-UNSUPPORTED-001`. Roadmap v1.4. |
| Anchor positioning | 📥 | Parsed; emits `CSS-ANCHOR-POSITIONING-UNSUPPORTED-001`. |
| `@media print` | ✅ | Default media in NetPdf. |
| `@media screen` | ✅ | Opt-in via `MediaType` option. |
| `@supports` | ✅ | |

---

## SVG (inline only)

| Feature | Status | Notes |
|---|---|---|
| Shapes (`rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path`) | ✅ | Rasterized via Skia (`<img>`-sourced SVG). Opt-in `HtmlPdfOptions.NativeSvgRendering` draws a SUPPORTED document (basic shapes + `<path>` + `<g>` + `transform`s + solid fill/stroke + `fill-rule` + root `viewBox`/`preserveAspectRatio`) as NATIVE PDF path ops (crisp at any zoom) via `SvgNativeEmitter`, falling back to raster (all-or-nothing) for anything richer (gradients/text/`<use>`/filters/masks). Default off → raster (byte-identical). |
| Fills, strokes | ✅ | `fill`/`stroke`/`stroke-width`/`fill-opacity`/`stroke-opacity`, `stroke-dasharray`/`-dashoffset`/`-linecap`/`-linejoin`/`-miterlimit`. |
| Lengths | ✅ | `px`/`pt`/unitless, `%` (vs the viewport), `em`/`rem` (vs font-size) — on shape / `<image>` / `<use>` / nested-`<svg>` geometry AND `<text>`/`<tspan>` coordinates (x/dx vs viewport width, y/dy vs height, em/rem vs the run font-size). |
| Element / group `opacity` | ✅ | A transparency layer (SaveLayer) composites the subtree at once. |
| Linear/radial gradients | ✅ | `url(#id)` paint servers; objectBoundingBox + userSpaceOnUse, spreadMethod, gradientTransform, href inheritance, radial focal. |
| `<pattern>` paint servers | ✅ | `fill`/`stroke="url(#id)"` tiles the pattern content (rendered once → Repeat shader); `patternUnits`/`patternContentUnits`/`viewBox`/`patternTransform`/`href` inheritance. Self-reference depth-bounded; tile rendered at user resolution (heavy scaling softens). |
| `clip-path` / `mask` references | ✅ | `clip-path="url(#id)"` unions a `<clipPath>`'s child geometry (clipPathUnits userSpaceOnUse + objectBoundingBox); `mask="url(#id)"` masks (maskContentUnits honored; `mask-type` luminance [default] + alpha, from an attribute or inline `style=`; the `maskUnits` region [objectBoundingBox -10%..120% / userSpaceOnUse] hard-clips the element). A url() to a non-clipPath/non-mask target is flagged + left un-clipped/un-masked. |
| 2D transforms | ✅ | `translate`/`scale`/`rotate`/`matrix`/`skewX`/`skewY`. |
| `<text>` / `<tspan>` | ✅ | Skia text shaping; `text-anchor`, dx/dy, %/em coords, `letter-spacing`/`word-spacing`, per-glyph `rotate` (global index across tspans), `dominant-baseline`, `textLength`/`lengthAdjust` (`spacing` + `spacingAndGlyphs`, single chunk; a whole-text scale for multiple chunks), font props, gradient fill. Complex-script deferred. |
| `<textPath>` | ✅ | Glyphs along ANY referenced basic shape (startOffset + text-anchor, tangent rotation, the shape's transform). `textLength`/`lengthAdjust` deferred. |
| `<marker>` references | ✅ | `marker-start`/`-mid`/`-end` (+ `marker` shorthand, inherited down `<g>`): markerWidth/Height (length units), refX/refY, markerUnits (strokeWidth/userSpaceOnUse), orient (auto/auto-start-reverse/angle), viewBox; vertices incl. the Z close. Curve tangents are EXACT (from the segment control points). |
| `<filter>` references | 🧪 | `filter="url(#id)"` — a filter GRAPH → a composed Skia image filter: `feGaussianBlur`/`feOffset`/`feColorMatrix` (matrix/saturate/hueRotate/luminanceToAlpha)/`feDropShadow`/`feFlood`/`feMerge`/`feComposite` (over/in/out/atop/xor/lighter/arithmetic)/`feBlend` (16 modes)/`feMorphology` (erode/dilate)/`feComponentTransfer` (identity/table/discrete/linear/gamma)/`feDisplacementMap`/`feConvolveMatrix`/`feTurbulence` (turbulence/fractalNoise)/`feDiffuseLighting`/`feSpecularLighting` (distant/point/spot lights)/`feImage` (a `data:` raster placed via `preserveAspectRatio`, or an element `href="#id"` rendered like `<use>`)/`feTile`, with named `in`/`in2`/`result` routing (incl. `SourceGraphic`/`SourceAlpha`) and per-primitive SUBREGIONS (`x`/`y`/`width`/`height` → a crop; `primitiveUnits=objectBoundingBox` maps the subregion fractions). Only the primary (reachable) tree contributes; `FillPaint`/`StrokePaint` (a solid color OR a gradient/pattern paint server over the bbox) + `BackgroundImage`/`BackgroundAlpha` (transparent) inputs are modeled; `flood-color`/`lighting-color` resolve `currentColor`; a forward/missing custom `result` ref is treated as unspecified (not flagged); the result is clipped to the filter region (explicit x/y/width/height + `filterUnits` objectBoundingBox/userSpaceOnUse — a zero/negative size renders nothing; else bbox + 10%). `primitiveUnits="objectBoundingBox"` remaps subregions + offset/blur/morphology/displacement lengths + light-source positions; a lighting `kernelUnitLength`, an external feImage href, and an unknown `filterUnits`/`maskUnits` value stay deferred (flagged). |
| `preserveAspectRatio` | ✅ | Full grammar — none/meet/slice + the nine x/y MIN/MID/MAX alignments — on `<image>` + nested viewports. |
| `<use>` / `<symbol>` / `<defs>` | ✅ | `<use>` clones with inherited paint; a `<symbol>`/`<svg>` target establishes a viewport (use/target width/height clip + viewBox scale + full preserveAspectRatio). |
| Nested `<svg>` viewport | ✅ | x/y/width/height clip + viewBox scale (full preserveAspectRatio). |
| `<image>` (raster) | 🧪 | A `data:` URI raster renders (full preserveAspectRatio). External/network href not fetched. |
| `<animate>`, SMIL | ❌ | Static document. |
| `<foreignObject>` | ❌ | No HTML-in-SVG embedding. |

---

## PDF features

| Feature | Status | Notes |
|---|---|---|
| PDF 1.7 emission | ✅ | Default. |
| PDF 2.0 emission (xref streams, object streams) | ✅ | Opt-in via `EmittedPdfVersion = V2_0`. |
| Font subsetting + ToUnicode CMap | ✅ | Searchable/copyable text. |
| Hyperlinks (Link annotations) | ✅ | |
| Outlines (bookmarks from headings) | 🧪 | Opt-in via `Features.GenerateOutlines`. |
| Tagged PDF / PDF/UA-1 | 📥 | Semantic IR built; emission deferred to v1.1. |
| PDF/A-3u | 📥 | Roadmap v1.2. |
| PDF/A-2u | 📥 | Roadmap v1.2. |
| AES-256 encryption | 📥 | Skip RC4 (broken). |

---

## Diagnostic codes

Every ❌ / 📥 / 🧪 entry above corresponds to a stable diagnostic code in `docs/diagnostics-codes.md`. Codes are versioned: once published, a code's meaning never changes; new codes are added for new conditions.

---

## PDF metadata strings

| Feature | Status | Notes |
|---|---|---|
| ASCII `Title` / `Author` / `Subject` / `Keywords` / `Creator` | ✅ | Emitted as PDF literal strings with §7.3.4.2 octal escaping for `(`, `)`, `\`, and bytes < 0x20 / > 0x7E. |
| Non-ASCII metadata (accented characters, CJK, emoji) | 🧪 | The public facade's `string` setters feed `PdfLiteralString`, which throws on `char > 0x7E`. Real-world metadata with non-ASCII characters needs routing through UTF-16BE-encoded `PdfHexString`. The byte writer already supports both; the gap is purely at the public surface. |
| Producer string | ✅ | Always emitted; defaults to `"NetPdf"`. |
| `CreationDate` / `ModDate` | ✅ | ISO 32000-2 §7.9.4 `D:YYYYMMDDHHmmSS{Z\|+HH'mm'\|-HH'mm'}` format. Default to omitted when not set so output is reproducible. |

---

## Determinism

| Feature | Status | Notes |
|---|---|---|
| Byte-equal output for byte-equal input | ✅ | Validated by the 75-test harness (`PdfDocumentDeterminismHarnessTests`): 18 document shapes × byte-equal-twice + byte-equal-thrice + structural sanity + per-platform SHA-256 pin. |
| Image dedup by content hash + dictionary | ✅ | Same image content used N times → single XObject. |
| Pinned `FlateDecode` compression level | ✅ | `PdfFormat.PdfDeflateCompressionLevel = SmallestSize` is shared by every stream emitter; pins the byte-stability premise of the deflate output. |
| Cross-platform byte-equality | 🧪 | Pinned per `OS-arch` key (currently `osx-arm64`); other platforms log "no pin, snapshot skipped" until they are captured in the containerized reference environment. |

Output is deterministic: identical input produces identical bytes.

---

Last updated: 2026-06-22.
