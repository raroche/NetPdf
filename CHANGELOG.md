# Changelog

All notable changes to NetPdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The repository is **private through Phase 5**; tagged releases below are git tags only — NuGet publication happens at the v1.0 launch event (per `docs/phases/phase-5-packaging-and-release.md`).

## [Unreleased]

The `0.7.0-beta` entry below is **prepared for tagging** — version bumped, CHANGELOG written, exit criteria signed off — but the git tag is created by the maintainer after PR merge. Until tagged, treat the section as the staged contents of the next release. (The earlier `0.3.0-alpha` entry is staged the same way.)

[Unreleased]: https://example.invalid/NetPdf/compare/0.7.0-beta...HEAD

## [0.7.0-beta] — staged for 2026-06-21 (tag pending PR merge)

Phase 3 — fragmentainer-aware layout + pagination. NetPdf now runs an HTML+CSS document **end-to-end to a multi-page PDF**: the Phase-2 box tree flows through block / inline / flex / grid / table / multicol / absolute layout, a pagination optimizer chooses the page breaks, paged media (`@page` + the 16 margin boxes + generated content) frames each page, and text shaping + painting + image embedding (Phase-5-interleaved) write the bytes. This is the first user-useful release — most business documents render correctly. The repository remains **private through Phase 5**; this is a git-tag-only milestone (no NuGet publication — that lands at v1.0).

### Added

#### Layout engine (`NetPdf.Layout`)
- **Block formatting** — margin collapsing, block formatting contexts, floats + `clear`, min/max/fit-content sizing, `box-sizing: content-box|border-box` on **both** axes (via the shared `BoxSizingHelper`), `LengthPx` min/max-width/height clamping (explicit + auto/fill), and §10.3.3 auto-margin centering (`margin: 0 auto`) — block, list-item, replaced, **and** flex/grid containers.
- **Inline + line layout** (`LineBuilder`) — UAX #9 bidi, HarfBuzz shaping, UAX #14 line breaking, `white-space`, `text-align` (incl. `justify` / `justify-all`), the full `vertical-align` set (incl. line-edge keyword line growth), the `line-height` cascade grammar, inline-block baseline metrics, `::first-line` / `::first-letter`.
- **Flexbox L1** — W3C subset **100%**: all four `flex-direction` values + `flex-wrap` (incl. `wrap-reverse`), `justify-content` / `align-items` / `align-self` / `align-content` (positional + safe/unsafe overflow), `flex-grow` / `flex-shrink` / `flex-basis` (length + auto) with the §9.7 step-4 min/max clamping iteration, `order`, `gap` / `column-gap` / `row-gap` gutters (consuming free space before grow/shrink + justify-content), explicit container `width` + `margin: 0 auto` centering, RTL `row` main-axis flip, anonymous-item wrapping, and multi-page container splitting.
- **CSS Grid L1** — W3C subset **93.3%**: track sizing (`auto` / `fr` / `minmax` / `fit-content` / `repeat` / `auto-fill` / `auto-fit`), sparse + dense auto-placement, `grid-template-areas`, `column-gap` / `row-gap` / `gap` gutters (incl. spanned items + auto-height extent), and cross-page row splitting with a row-extent memo.
- **Tables** — auto + fixed layout, collapse/separate borders, row/colspan, `<thead>`/`<tfoot>` repeat, intra-cell row splitting across pages. **Multicol**, **absolute** + `position: fixed`.

#### Pagination engine (`NetPdf.Paginate`)
- Break resolver, a documented cost model, a bounded-DP optimizer (≤2-page lookahead), continuation tokens, checkpoint/rewind, and a bounded-retry coordinator. Prose paginates at block granularity (a text block whose margin box overflows breaks whole to the next page).

#### Multi-page driver
- The page-emitting driver loop with nested-container fragmentation, per-page counters, cross-page running content, per-page `@page :first` / `:left` / `:right` / `:blank`, named pages, and font de-duplication across pages.

#### Paged media + generated content
- `@page` rules + the 16 margin boxes (style / border / padding / background / border-radius / overflow), `string()` / `string-set` (incl. the `content()` form), `element()` / `position: running()`, and `counter(page)` / `counter(pages)` with counter styles.

#### Paint + PDF emission (Phase-5-interleaved)
- `TextPainter` (shape → subset → embed), `FragmentPainter` (background-color/-image, borders, outline, border-radius, tiling patterns), the image pipeline (`<img>` + `background-*`, `object-fit` / `-position`, `data:` URIs, Skia raster fallback), all writing through **our** PDF byte writer (`NetPdf.Pdf`).

#### W3C conformance suite (`tests/NetPdf.W3cConformance`)
- A **curated assertion suite** (not vendored WPT) that drives the internal pipeline (`Phase2Pipeline` → `BlockLayouter`) and asserts `BoxFragment` geometry, gated per-case (every non-`KnownGap` case must pass; every `KnownGap` case must still fail). Published pass-rates: **CSS 2.2 93.3%** (28/30), **Fragmentation 90.0%** (9/10), **Flexbox 100%** (18/18), **Grid 93.3%** (14/15).

#### Performance + memory gates
- `PerformanceGateTests` enforce a 3-page invoice (~42 ms) + a 22-page report (~400 ms) on the synthetic-font layout pipeline, and a retained-heap gate (flat across page count).

### Test counts

- **7,156 unit tests** (+3 skipped), 30 layout snapshots, 97 real-document goldens, 4 W3C conformance gates, pagination golden, rendering corpus, PDF validation, AOT/JIT byte-parity, and determinism — all green at **0 warnings** in Release.

### Notes / known limitations

- **Perf + memory exit criteria are smoke-gated / partial, not fully met.** The perf gate exercises a synthetic-font layout pipeline (no images / web fonts); the full-pipeline BenchmarkDotNet target is not yet a build gate. Retained heap is flat, but allocation scaling across pages is super-linear (`multi-page-allocation-churn`).
- **Documented deferrals** (none block a conformance criterion): auto-height shrink-to-fit of the emitted box (`auto-height-emit-vs-pagination`), percentage min/max sizing (`min-max-percentage-sizing`), grid `fr` tracks + gap (`grid-gap-fr-track-sizing`), percentage gaps (`gap-percentage-sizing`), and single-paragraph line splitting (`inline-only-block-line-splitting`). Full inventory: `docs/deferrals.md`.
- **Tagged PDF / PDF/UA / PDF/A are post-v1.** The semantic IR is built alongside layout but tagged structure is not emitted in v1.

## [0.3.0-alpha] — staged for 2026-05-07 (tag pending PR merge)

Phase 2 — CSS engine + DOM pipeline. NetPdf can now run an HTML+CSS document through the full Phase 2 pipeline (parse → preprocess → cascade → var → calc → typed-property resolve → box-tree generation → semantic-tree generation) and produce a styled, paginatable box tree paired with an accessibility-ready semantic tree. The PDF byte-writer from Phase 1 remains intact; the missing piece between "HTML in" and "PDF bytes out" is Phase 3 (layout + pagination) + Phase 4 (paint).

### Added

#### HTML parsing host (`NetPdf/HtmlParsingHost`)
- AngleSharp 1.x configured with scripting **disabled** + resource loading routed through `IResourceLoader` (no AngleSharp resource fetcher leakage). `IsKeepingSourceReferences = true` so HTML diagnostics carry line/column.
- `<script>` element stripping with `HTML-SCRIPT-IGNORED-001` Warning per element.
- `javascript:` URL stripping on `<a>`/`<area>` `href` + any element's `xlink:href` with `HTML-JAVASCRIPT-URL-IGNORED-001` Warning.
- HTML diagnostics flow through the public `IDiagnosticsSink` set on `HtmlPdfOptions.Diagnostics`.

#### CSS parser adapter (`NetPdf.Css.Parser`)
- AngleSharp.Css 1.0.0-beta.144 bridged to the internal AST. `CssParserAdapter` produces `CssStylesheet` records carrying selectors, declarations, source location, media query, owner-element pairing, and disabled state.
- `CssPreprocessor` recovery layer: detects + restores authored text for modern functions/values that AngleSharp.Css 1.0.0-beta.144 silently corrupts or drops (`oklch()`, `oklab()`, `lab()`, `lch()`, `color()`, `color-mix()`, `light-dark()`, modern multi-arg `attr(name type, fallback)`).
- Owner-node-based stylesheet pairing (`<style media="...">` reads media via owner-element walk, not via fragile ordinal pairing).

#### Source-generated property tables (`NetPdf.SourceGen`)
- `PropertyId` enum + `PropertyMetadata.Table` + `PropertyMetadata.NameToId` (`FrozenDictionary` for case-insensitive O(1) lookup) generated at compile-time from `properties.json`. **Single source of truth** — adding a CSS property is a JSON entry + rebuild.
- Source-gen emits NPDFGEN0001..0005 diagnostics on schema violations (missing field, duplicate id, malformed value).

#### `ComputedStyle` (`NetPdf.Css.ComputedValues`)
- 8-byte `ComputedSlot` value type with a tag in the high byte: `Unset`, `Color`, `CurrentColor`, `LengthPx`, `Integer`, `Number`, `Percent`, `Keyword`, `Inherit`, `Initial`, `Revert`, `Side`. Larger values (font-family lists, gradients) keyed by index into rented side tables.
- `ComputedStyle` is a `sealed class` (NOT a struct) holding two `[InlineArray]`-backed inline buffers — one of `ComputedSlot`s sized to `PropertyMetadata.Count`, one of `ulong` bitmap words for the explicit-set flag. Pooled via a process-wide `ConcurrentBag<ComputedStyle>` (NOT `ArrayPool<T>`); `Rent()` clears via `Reset()` on take, `Dispose()` returns to the bag with a soft-guard `_disposed` flag.
- Custom properties (`--*`) live in a lazily-allocated `Dictionary<string, ComputedSlot>` on `ComputedStyle`; var() substitution reads through.

#### Selector compiler + matcher (`NetPdf.Css.Selectors`)
- AngleSharp selector AST → bytecode-style compiled selectors with right-to-left evaluation.
- Bloom filter on each element's `(tag, classList, id)` accelerates rejection. Specificity computed at compile time.
- Compound selectors, descendant / child / sibling combinators, attribute selectors, `:hover` / `:focus` / `:checked` / `:nth-child(...)` / `:has(...)` / `:not(...)`. `::before` / `::after` / `::marker` / `::first-line` / `::first-letter` produce pseudo-element selectors with separated rule storage.

#### Cascade resolver (`NetPdf.Css.Cascade`)
- Specificity / origin / importance / `@layer` ordering + source-order tie-breaking per CSS Cascade L4. UA → User → Author per origin priority.
- `@layer` ordering with proper later-layer-wins semantics.
- `:has()` selector parsed + bytecode-flagged; the matcher **always returns false** at runtime in v1 + emits `CSS-HAS-RENDERING-NOT-IMPLEMENTED-001` once per flagged sub-group encountered (deferred to v1.4 when proper `:has()` matching lands).
- `@container` parsed; rendering emits `CSS-CONTAINER-QUERY-UNSUPPORTED-001` (Roadmap v1.4).

#### `var()` substitution (`NetPdf.Css.ComputedValues.VarResolver`)
- Custom property substitution with circular-reference detection (`CSS-VAR-CIRCULAR-001`) + expansion limit (`CSS-VAR-EXPANSION-LIMIT-001`).
- Fallback chains (`var(--x, var(--y, 12px))`).

#### `calc` resolver (`NetPdf.Css.Cascade.CalcResolver`)
- Recognizes `calc()` / `min()` / `max()` / `clamp()` / `abs()` / `sign()` per CSS Values L4 §10. **Subset contract** — fully reduces expressions whose operands are absolute lengths (px, in, cm, mm, q, pt, pc), percentages, angles (deg/rad/grad/turn), times (ms/s), frequencies (hz/khz), or resolutions (dppx/x/dpi/dpcm). Context-relative operands (font-relative `em`/`rem`/`ch`/`ex`/`ic`/`cap`/`lh`/`rlh`; viewport-relative `vw`/`vh`/`vmin`/`vmax` + small/large/dynamic variants; container-relative `cqw`/`cqh`/`cqi`/`cqb`/`cqmin`/`cqmax`) **defer**: the original function text is preserved verbatim for the typed-value pipeline (Tasks 10+) or Phase 3 layout to revisit once font matching + viewport size + container queries are known. Mixed-unit operations across incompatible dimensions also defer.
- Division-by-zero detection emits `CSS-CALC-DIV-BY-ZERO-001`.
- Syntactically broken expressions (trailing tokens, malformed clamp arity, etc.) emit `CSS-CALC-INVALID-001`.

#### Per-property typed resolvers (`NetPdf.Css.ComputedValues.PropertyResolvers`)
- **Length resolver** — absolute lengths fully resolve to pixels (px, in, cm, mm, q, pt, pc) plus percentages. **Context-relative units defer** to Phase 3's typed-value pipeline once font metrics + viewport + container size are known: font-relative (`em`/`rem`/`ch`/`ex`/`ic`/`cap`/`lh`/`rlh`), viewport-relative (`vw`/`vh`/`vmin`/`vmax` + `svw`/`lvw`/`dvw` variants + `vi`/`vb`), container-relative (`cqw`/`cqh`/`cqi`/`cqb`/`cqmin`/`cqmax`). The deferred raw text is preserved on the cascade entry for the typed pipeline to consume.
- **Color resolver** — 147 named colors per Color L4 §6.1, 20 system colors per §10, hex notation, `rgb()`/`rgba()` legacy + modern, `hsl()`/`hsla()` legacy + modern with strict syntax per Color L4 §4.2.1/§4.3.1, `currentcolor` sentinel.
- Number, Integer, Keyword resolvers (per-property keyword tables generated from `properties.json`).
- Modern color functions (`oklch`, `oklab`, `lab`, `lch`, `color`, `color-mix`, `light-dark`) parsed via preprocessor recovery; cycle-1 emits `CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001` Info; typed evaluation lands cycle-2.
- `font-weight` resolved to numbers per Color L4 § 4.

#### `Box` hierarchy + `BoxKind` enum (`NetPdf.Layout.Boxes`)
- Typed enum: `Root`, `BlockContainer`, `InlineBox`, `InlineReplacedElement`, `ListItem`, `Marker`, `AnonymousBlock`, `TextRun`, `LineBreak`, `Table`, `TableGrid`, `TableHeaderGroup`, `TableRowGroup`, `TableFooterGroup`, `TableRow`, `TableColumn`, `TableColumnGroup`, `TableCell`, `TableHeaderCell`, `TableCaption`, `InlineBlockContainer`. `Box` carries `SourceElement?`, `Pseudo`, `Style`, `Children`, optional `FirstLineStyle` / `FirstLetterStyle` (Phase 3 line layout consumes).

#### `BoxBuilder` (`NetPdf.Layout.Boxes.BoxBuilder`)
- DOM walk + display dispatch + anonymous-block insertion per CSS Display L3 §3.
- `HtmlDefaultDisplay` UA defaults table per HTML "Rendering" §15.3 (incl. metadata-content `display: none`); `HtmlReplacedElements` covers img/video/audio/canvas/iframe/object/embed.
- `DisplayMapper` produces `(BoxKind, ResolutionOutcome)` from `(display, element)`. Replaced-element exception per Tables L3 §2.
- Anonymous block insertion per Display L3 §3.1 (block containers with mixed inline + block children).
- `display: contents` per Display L3 §3.1.1 (children promoted to grandparent).
- Table fixup per Tables L3 §3 (single `TableGrid` child + auto-wrapping bare cells/rows in synthesized row-groups + caption position preservation + column child filtering + tree-wide orphan fixup + replaced-element exception + anon-box style isolation).
- Pseudo-element materialization per CSS Pseudo L4: `::before` / `::after` with `content: ` parsing (string + multi-string concat + `attr(name)` substitution + escape decoding); `::marker` per Lists L3 §3 with 12 `list-style-type` keywords (disc, circle, square, decimal, decimal-leading-zero, lower/upper-roman, lower/upper-alpha, lower/upper-latin, lower-greek); `::first-line` / `::first-letter` style staging on the host box for Phase 3 line layout to apply during fragment rendering.
- Replaced-element pseudo suppression per Pseudo L4 §3 with diagnostic dedup.

#### `SemanticTreeBuilder` (`NetPdf.Layout.Semantic`)
- Parallel walk producing 26 PDF/UA-aligned roles (Document, Heading1..6, Paragraph, BlockQuote, Code, List, ListItem, Table family, Link, Image, Figure, FigureCaption, Header / Footer / Nav / Main / Aside / Article / Section, InlineText).
- Cascade-aware hidden-element exclusion (`display: none`, `visibility: hidden`, ARIA `aria-hidden="true"`, HTML5 `hidden`, metadata-content tags).
- Image alt tri-state per HTML5 §4.8.3 (`null` for missing, empty string for explicit `alt=""` decorative, normalized non-empty for explicit alt; aria-label fallback).
- Table-cell metadata captured (rowspan, colspan, scope, headers, abbr) per HTML5 §4.9.10 + §4.9.12.
- `<a>` without `href` is transparent per HTML5 §4.6.1.
- `<pre>` / `<code>` preserve descendant text whitespace per HTML5 §15.3.5.
- v1 policy on generated content: `::before` / `::after` / `::marker` text intentionally absent (DOM-only walk; box-tree-driven rebuild for v1.1 PDF/UA).

#### Diagnostics (`docs/diagnostics-codes.md`)
- 18 new CSS-* + HTML-* codes registered (CSS-PROPERTY-VALUE-INVALID-001, CSS-PARSE-WARNING-001, CSS-AT-RULE-UNKNOWN-001, CSS-VAR-CIRCULAR-001, CSS-VAR-EXPANSION-LIMIT-001, CSS-CALC-INVALID-001, CSS-CALC-DIV-BY-ZERO-001, CSS-CONTAINER-QUERY-UNSUPPORTED-001, CSS-HAS-RENDERING-NOT-IMPLEMENTED-001, CSS-CONTENT-FUNCTION-UNSUPPORTED-001, CSS-ATTR-MULTI-ARG-UNSUPPORTED-001, CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001, CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001, HTML-SCRIPT-IGNORED-001, HTML-JAVASCRIPT-URL-IGNORED-001).
- Internal `CssDiagnostic` mirrored to public `Diagnostic` via `PublicDiagnosticsSinkAdapter`. A single sink set on `HtmlPdfOptions.Diagnostics` collects HTML + CSS + layout diagnostics.

#### `Phase2Pipeline` (`NetPdf/Phase2/Phase2Pipeline`)
- Canonical pipeline composition: `RunFromHtmlAsync(html, options, sink, ct)` + `Run(document, sheets, options, sink, ct)`. Returns `Phase2Result(BoxRoot, SemanticRoot, ResolvedCascade, AdaptedSheets)`.
- `CancellationToken` honored at every stage boundary.
- `CssMediaContext` built from `HtmlPdfOptions` (MediaType + PageSize → media query evaluation + viewport).

#### Layout snapshot test infrastructure (`tests/NetPdf.LayoutSnapshots`)
- 9 fixture pairs covering principal Phase 2 surface area: simple paragraph, ordered list with markers, table with caption + scope + colspan, var/calc value chain (with paired computed-value Fact), pseudo-element with attr() substitution, hidden + aria filtering, figure + image alt tri-state, unsupported CSS features (counter() + oklch() + img::before — 3 distinct codes), `<script>` + `javascript:` URL stripping (HTML diagnostics with source location).
- 3 deterministic serializers: `BoxTreeSerializer`, `SemanticTreeSerializer`, `DiagnosticsSerializer`. Auto-discovered fixture directories.
- `NETPDF_UPDATE_SNAPSHOTS=1` regenerates goldens.
- Source-tree path resolution walks up to `NetPdf.slnx` so updates write to source, not `bin/`.

#### Phase 2 pipeline benchmark (`tests/NetPdf.Benchmarks/Phase2PipelineBenchmarks`)
- 5 benchmarks: 4 corpus invoices (3.7–13.6 KB) + 100 KB synthetic invoice (Phase 2 exit-criterion gate).
- First-cycle baseline (Apple M4 Pro, .NET 10.0.7, macOS arm64, in-process toolchain). Statistics are extracted from BenchmarkDotNet's `*-report-full-compressed.json` so p50 / p25 / p75 are explicit:
  - 01-classic-pure-css.html (3.7 KB): mean **561 µs**, 1.43 MB allocated.
  - 02-tailwind-cdn.html (10.8 KB): mean **547 µs**, 1.34 MB.
  - 03-tailwind-cdn-responsive.html (13.6 KB): mean **702 µs**, 1.61 MB.
  - 04-anvil-running-elements.html (6.3 KB): mean **717 µs**, 1.58 MB.
  - 100 KB synthetic invoice: **mean 51.6 ms / p50 51.4 ms / p25 51.3 ms / p75 51.9 ms / 51.3 MB**.
- **Phase 2 exit-criterion #7 (`< 50 ms p50` for 100 KB invoice) is documented as borderline-waived for the `0.3.0-alpha` ship**, not satisfied: the measured p50 is 51.4 ms, ~2.8% over target. The waiver rationale is documented in the benchmark's XML doc + the Phase 2 doc's exit-criterion checkbox: cycle-1 prioritizes correctness over perf; the 100 KB synthetic stresses cascade-matching at ~3700 elements which is an O(rules × elements) hot path; Phase 3 layout/paint will dominate total convert wall-clock anyway (Phase 2 pipeline becomes ~1% once fragmentainer-aware layout lands), so deferring the optimization until Phase 3+5 lets us use rendering-bound measurements instead of synthetic-bound ones to set the perf budget. The benchmark + JSON capture stay green for Phase 5 to pin once the full convert path is wired.

### Test counts
- **3220 unit tests** + 96 RealDocuments + 30 LayoutSnapshots = **3346 tests passing** (1 skipped pin-capture utility).
- AOT/JIT byte-parity gate from Phase 1 remains green.

### Notes / known limitations
- `HtmlPdf.Convert(html)` still throws `NotImplementedException`. Phase 2 produces the styled box tree; Phase 3 wires layout/pagination, Phase 4 wires paint, then the public facade returns real PDF bytes.
- External `<link rel="stylesheet">` resource loading is disabled at the AngleSharp boundary in v0.3.x; Phase 5 wires the resource pipeline.
- Modern color functions (`oklch`/`oklab`/`lab`/`lch`/`color`/`color-mix`/`light-dark`) parse via preprocessor recovery + emit `CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001`; typed evaluation lands cycle-2.
- `::before`/`::after`/`::marker` generated text is materialized in the box tree but intentionally absent from the semantic tree per the v1 PDF/UA policy. v1.1 PDF/UA pass re-sources the semantic tree from the rendered box tree.
- AngleSharp.Css 1.0.0-beta.144 silently drops `display: contents` + `::marker` selectors during parse. Implementations are correct + load-bearing once cycle 2 wires preprocessor recovery for these.
- CSS source positions (line/column) are wired through plumbing but emit `CssSourceLocation.Unknown` until Task 3 cycle-2 lands real source tracking.

## [0.1.0-alpha] — 2026-05-03

Phase 1 — PDF writer + text foundation. NetPdf can now produce well-formed, byte-deterministic PDF 1.7 bytes programmatically from internal `PdfDocument` calls (no HTML pipeline yet — that ships in Phase 2). The PDF byte writer, the international text shaping subsystem (UAX #9 / #14 / #29 conformance), the OpenType font subsetting + ToUnicode CMap pipeline, the WOFF 1.0 / 2.0 decoders, the JPEG / PNG / WebP / AVIF / GIF image embedders with content-hash dedup and indirect `/SMask` wiring, the determinism harness with a per-platform pin map, the AOT-clean smoke binary with an enforced JIT/AOT byte-parity gate, and the BenchmarkDotNet baseline with a +25%-tolerance regression gate are all in place.

### Added

#### PDF byte writer (`NetPdf.Pdf`)
- Full `PdfObject` hierarchy: 11 concrete types (Boolean, Null, Integer, Real, Name, LiteralString, HexString, Array, Dictionary, Stream, IndirectRef) emitting bytes per ISO 32000-2:2020 §7.3. `PdfDictionary` uses `OrderedDictionary<TKey,TValue>` for deterministic insertion-order preservation.
- `IndirectObjectStore` with 1-based deterministic numbering, `Allocate` / `Add` / `Assign` / forward references, byte-offset recording, and `ValidateAllAssigned` gate.
- `PdfDocumentWriter` orchestrating header → indirect objects → xref → trailer → `startxref` → `%%EOF`. xref entries exactly 20 bytes (§7.5.4); 4-byte binary marker comment (§7.5.2); `MaxXrefByteOffset = 9_999_999_999` enforced (4 boundary unit tests cover the throw).
- Auto-derived trailer `/ID` from SHA-256 of header + indirect objects + xref (§14.4) — content-addressing for free.
- `PdfPreflightValidator` runs structural checks before any byte is written: version on allow-list, all slots assigned, `/Root` shape, explicit `/ID` shape, foreign-store reference rejection, direct-cycle + indirect-cycle detection, no nested direct streams (streams must be indirect per §7.3.8).
- `ContentStreamWriter` with `q` / `Q` / `cm` / `re` / `f` / `S` / `Tj` / `TJ` / `Do` / BMC / BDC / EMC operators; `ContentStreamBuilder` facade with optional FlateDecode compression at the centrally-pinned `PdfFormat.PdfDeflateCompressionLevel = SmallestSize`.
- `DisplayCommand` 64-byte tagged-union IR + `DisplayList` buffer (Phase 2/3 paint output).

#### Font subsetter & embedding
- `OpenTypeFont` parser covering `head` / `hhea` / `maxp` / `cmap` / `loca` / `glyf` / `name` / `OS/2` / `post` / `hmtx` (TTF) **and** `CFF` / `CFF2` headers (OTF). Read-only span-based; AOT-clean. **Parsing scope only** — see embedding scope immediately below.
- **TTF embedding pipeline (shipped):** glyph subsetter with composite-glyph closure traversal + `glyf`/`loca` re-numbering; new `cmap` formats 4 + 12 (BMP + supplementary planes); `ToUnicode` CMap generator with deterministic per-glyph mapping (preserves searchable/copyable text without requiring tagged PDF); Type 0 / CIDFontType2 wrapper with full `/FontFile2` + `/FontDescriptor` + `/DescendantFonts` graph; 6-letter deterministic `BaseFont` prefix (SHA-256 of name + glyph bitmap → first 6 hex digits → base-26).
- **OTF / CFF embedding — deferred to Phase 1.x.** `0.1.0-alpha` does NOT ship CFF subsetting or the `FontFile3` / `CIDFontType0C` emit path. `EmbeddedTtfFont.Build` throws `InvalidOperationException` with a clear "OTF/CFF embedding deferred" message when called on a CFF-flavored `OpenTypeFont` — consumers get a precise signal rather than a malformed PDF. CFF2 (variable-fonts) is post-v1.0 work.

#### Text shaping (`NetPdf.Text`)
- HarfBuzzSharp wrapper (`HbShaper.Shape`) producing glyph runs with kerning + ligature + script-aware shaping.
- **UAX #9 Bidi** — full implementation (P / X / W / N / I / L rules + BD7/BD13 segmentation). UCD `BidiTest.txt` + `BidiCharacterTest.txt` at **100% conformance**.
- **UAX #14 Line Breaking** — pair-table-driven LB rules. UCD `LineBreakTest.txt` at **99.952% conformance**.
- **UAX #29 Grapheme Cluster Boundaries** — UCD `GraphemeBreakTest.txt` at **100% conformance**. (Word and sentence boundaries — stages 14.2 / 14.3 — are post-Phase-1.)
- **Liang hyphenation** with English (`en-us`) bundled patterns + exceptions list. Span-based hot-path lookup via `FrozenDictionary.GetAlternateLookup<ReadOnlySpan<char>>`.
- **Font registry** + cross-platform system font enumeration (macOS, Windows, Linux, Alpine). Per-document `FontRegistry` with composite key `(Family, WeightCss, StretchCss, Italic)`. Spec-accurate CSS Fonts 4 §5.2 ordered tier search (stretch direction → style → weight regime).
- **WOFF 1.0** decoder (zlib) and **WOFF 2.0** decoder (Brotli + glyf/loca transform reverse via the 128-entry §5.2 triplet decode table). Real Roboto-Regular.woff2 fixture exercises the full pipeline.

#### Image embedding (`NetPdf.Pdf.Images`)
- **JPEG passthrough** — strict SOI → SOFn → SOS → EOI walk; only SOF0 / SOF2 + 8-bit accepted. Adobe-inverted CMYK detected and emitted with `/Decode [1 0 1 0 1 0 1 0]`. ICC profiles tolerated.
- **PNG embedder** with four paths: opaque passthrough (`Predictor 15`), color-key `/Mask` for binary tRNS, alpha-split SMask for RGBA / GA, indexed-with-non-binary-tRNS via SMask. CRC-32 + structural validation. Bounded zlib decompression for trust-boundary defense.
- **WebP / AVIF / GIF** via SkiaSharp decode → RGB plane + optional alpha plane → FlateDecode. 25-megapixel cap. AVIF fixture (BSD-2-Clause libavif test corpus) committed; tests no-op gracefully on hosts without libavif (macOS).
- **`PdfDocument.RegisterImage(ImageXObjectResult)`** wires the indirect `/SMask` reference correctly: clones the primary image dictionary before assigning so the caller's builder output stays immutable across registrations. Same-instance dedup works; cross-document reuse works.
- **Content-hash dedup** keys on payload + dictionary canonical bytes (SHA-256 of both). Two pixel-identical buffers with different `/ColorSpace` or `/Filter` remain distinct.

#### High-level `PdfDocument` builder
- `AddPage(MediaBoxSize)` / `RegisterImage(PdfStream | ImageXObjectResult)` / `Save()` / `SaveTo(IBufferWriter<byte>)` API. Single-use document model (throws on second `Save()`).
- Info dict metadata: Title / Author / Subject / Keywords / Creator / Producer / CreationDate / ModDate. ISO 32000-2 §7.9.4 date format `D:YYYYMMDDHHmmSS{Z|+HH'mm'|-HH'mm'}`. Defaults to omitted dates for reproducibility.
- Image XObject shape validation: `/Subtype /Image`, Width > 0, Height > 0, ColorSpace, BitsPerComponent ∈ {1,2,4,8,16} (and {8,16} for SMasks per §11.6).

#### Determinism harness
- 18 document shapes × 4 property tests (byte-equal-twice, byte-equal-thrice, structural sanity, per-platform SHA-256 pin) = **72 parameterized determinism tests**.
- Per-platform pin map keyed by `OS-arch` (`osx-arm64` pinned; other platforms log "no pin, snapshot skipped" until Phase 5 captures them in the containerized reference environment).
- `DeterminismDiagnostics.AssertByteEqualsWithDiagnostics` produces diagnosable failures: first-diff offset + per-half SHA + hex/ASCII context windows.
- Error-path determinism: `RegisterImage(invalid)` and double-`Save()` must throw the same exception type and the same `Message` across runs.
- Explicit `/ID` extraction test (`Canonical_document_has_deterministic_trailer_ID`).
- `PdfFormat.PdfDeflateCompressionLevel = SmallestSize` pins the deflate level so all stream emitters produce byte-equal output for byte-equal input.

#### AOT smoke + parity gate
- `tests/NetPdf.AotSmoke/` publishes a 1.5 MB native binary via `dotnet publish -p:PublishAot=true`. Exercises JPEG passthrough + transparent-GIF SMask path through `PdfDocument`.
- `SmokeDocumentFactory` shared between the AOT entry point and the parity test — drift between them is impossible.
- `tests/NetPdf.UnitTests/Pdf/AotJitParityTests.cs` (4 tests): asserts the native binary's SHA-256 output equals the JIT factory's. Negative-path verified — perturbing the JIT factory causes the parity test to fail with a diagnosable SHA mismatch.
- `scripts/aot-parity.sh` — single-command publish + parity-gate run for CI.

#### Performance baseline + regression gate
- 6 benchmark classes split per concern (PageScaling, SinglePage, ImageEmbedding, Dedup, Streaming, MixedDocument) → 23 parameterized benchmarks.
- `[MemoryDiagnoser]` on every benchmark; Workstation GC pinned for cross-host stability.
- Per-platform baseline JSONs committed under `tests/NetPdf.Benchmarks/baselines/phase-1-osx-arm64/`.
- `scripts/benchmark-gate.sh` — single-command gate. Runs the suite, exports JSON, invokes `Program.cs --compare BASELINE-DIR CURRENT-DIR [tolerance]`, exits 1 if any benchmark Mean exceeds +25% of baseline. Negative-path verified.
- Phase 1 baseline highlights (Apple M4 Pro, .NET 10.0.7, macOS arm64, Workstation GC):
  - Single blank A4 page → bytes: **5.6 µs**, 7.45 KB.
  - 1000 blank pages → bytes: **2.59 ms**, 2.6 MB (linear at ~2.46 KB/page across 4 orders of magnitude).
  - JPEG passthrough single page: **9.2 µs**.
  - PNG RGBA + indirect SMask: **19.1 µs**.
  - WebP opaque via raster: **19.0 µs**.
  - Cache-hit-isolated dedup per call: **2.9 µs** (true per-call cost via `[OperationsPerInvoke = 99]`).
  - `SaveTo(IBufferWriter<byte>)` streaming path: **8.9 µs** (3% faster wall-clock vs `Save() → byte[]`).
- All Phase 1 wall-clock targets crushed: 100-page < 500 ms target = 264.7 µs actual ≈ **~1,890× headroom**.

#### Documentation
- [docs/design/determinism.md](docs/design/determinism.md) — public determinism contract, enforcement, re-pin protocol, known gaps.
- [docs/design/aot-smoke.md](docs/design/aot-smoke.md) — AOT-clean contract, banned patterns, run protocol, common AOT failure modes.
- [docs/design/performance.md](docs/design/performance.md) — performance baseline, targets vs. actual, regression-gate protocol, GC mode rationale, comparison with the wkhtmltopdf / Chromium-print / PDFsharp ecosystem.
- [docs/compatibility-matrix.md](docs/compatibility-matrix.md) — added "PDF metadata strings" + "Determinism" sections.
- Per-task subtask log in [PROGRESS.md](PROGRESS.md) covers tasks 1–25 with rationale and verification.

### Test counts
- **1546 unit tests** + 11 cross-project tests = 1557 total passing (1 skipped pin-capture utility).
- **72 determinism harness tests** (18 shapes × 4 property checks).
- **4 JIT/AOT parity tests** (factory determinism + native binary parity, including output-path verification).
- **23 BenchmarkDotNet benchmarks** with per-platform pinned baseline + +25% regression gate.

### Phase 1 exit criteria status
1. ✅ All Phase 1 task tests pass.
2. ✅ UCD reference tests pass: UAX #9 100%, UAX #14 99.952%, UAX #29 (grapheme stage) 100%.
3. ✅ Byte-determinism test passes (per-platform SHA-256 pin + 72 property tests).
4. ✅ AOT smoke publishes and runs successfully on `osx-arm64` (Linux + Windows pinned in Phase 5 CI matrix).
5. ✅ Programmatic `PdfDocument` construction works end-to-end and produces valid PDF 1.7 bytes that open in Acrobat / Firefox / Chrome / macOS Preview (verified via the AOT smoke output).
6. ⏸️ qpdf `--check` deferred to Phase 5 CI — `qpdf` is not yet a local dev dep; Phase 5 wires it into the cross-platform validation matrix.
7. ✅ Roboto-Regular WOFF 2.0 loads, subsets, and embeds end-to-end.
8. ✅ Performance baseline meets target (~1,890× headroom on the 100-page metric).
9. ✅ CHANGELOG updated, `0.1.0-alpha` tagged.

### Known gaps (post-Phase-1)
- **Word boundaries (UAX #29 stage 14.2)** and **sentence boundaries (UAX #29 stage 14.3)** — needed by Phase 2/3 layout, deferred from Phase 1's grapheme-cluster scope.
- **Non-ASCII metadata** — `PdfLiteralString` rejects `char > 0x7E`; the Phase 2 facade will route via `PdfHexString` (UTF-16BE) automatically. Documented in `docs/compatibility-matrix.md`.
- **Cross-platform pinned hashes** for `linux-x64`, `linux-arm64`, `win-x64` — captured by the Phase 5 CI matrix when it lands.
- **AVIF benchmark** — deferred to Phase 5 cross-platform CI (libavif unavailable on macOS dev hosts).
- **`HtmlPdf.Convert(html)`** — public facade not yet wired (Phase 2 work). Phase 1 ships only the byte-writer surface; the public API throws `NotImplementedException` on call.

### Phase 0
- Initial scaffolding.
- Apache-2.0 license established.
- Clean-room policy documented (`docs/clean-room-policy.md`).
- Dependency dossier opened (`docs/legal/dependency-dossier.md`).
- Compatibility matrix published (`docs/compatibility-matrix.md`).
- Diagnostics code registry started (`docs/diagnostics-codes.md`).
- PDF spec notes & errata index opened (`docs/pdf-spec-notes.md`).
- Per-phase execution guides under `docs/phases/`.
- Project-scoped skills under `.claude/skills/`.
- `CLAUDE.md` session-bootstrap added at repo root for any future Claude session.
- Secrets-and-credentials policy documented (`docs/secrets-and-credentials.md`).
- Public API surface frozen: `HtmlPdf`, `HtmlPdfOptions`, `PdfRenderResult`, `IResourceLoader`, `IFontResolver`, `IDiagnosticsSink`, `FeatureFlags`, `SecurityPolicy`, `CachePolicy`.
- Solution scaffolding for nine source projects and eleven test projects.
- NuGet package ID `NetPdf` reserved on nuget.org via `0.0.1-phase0` placeholder (unlisted).
- `NUGET_API_KEY` set up as GitHub Actions repository secret with `NetPdf*` glob scope.

[0.1.0-alpha]: https://example.invalid/NetPdf/compare/0.0.1-phase0...0.1.0-alpha
[0.3.0-alpha]: https://example.invalid/NetPdf/compare/0.1.0-alpha...0.3.0-alpha
[0.7.0-beta]: https://example.invalid/NetPdf/compare/0.3.0-alpha...0.7.0-beta
