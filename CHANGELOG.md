# Changelog

All notable changes to NetPdf are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The repository is **private through Phase 5**; tagged releases below are git tags only — NuGet publication happens at the v1.0 launch event (per `docs/phases/phase-5-packaging-and-release.md`).

## [Unreleased]

The `0.7.0-beta` entry below is **prepared for tagging** — version bumped, CHANGELOG written, exit criteria signed off — but the git tag is created by the maintainer after PR merge. Until tagged, treat the section as the staged contents of the next release. (The earlier `0.3.0-alpha` entry is staged the same way.) Post-`0.7.0-beta` improvements accumulate under **Unreleased** below until the next release is cut.

### Fixed — Phase 3 residual long-tail

This batch closes the documented Phase-3 residual deferrals (`deferrals.md`) in a single pass; every change is gated so non-feature rendering stays byte-identical.

- **CSS Grid named-line occurrence + span-by-name placement** (CSS Grid L1 §8.3, `grid-implicit-named-area-and-occurrence-syntax-deferral` narrowed) — `GridSizing.ReadPlacement` previously fell back to auto-placement (with a deferral-tagged diagnostic) for the `<integer> <custom-ident>` and `span <custom-ident>` line forms. Now: **`grid-row-start: foo 2`** (and the column / end longhands) resolves to the Nth line literally named `foo` (`ResolveNamedLineOccurrence`; positive 1-based, negative from the end); **`grid-row-end: span foo`** / `span foo 2` (on the end edge with a definite start) spans to the Nth `foo` line after the start (`ResolveSpanToNamedLine`); and **`[foo-start] … [foo-end]` line pairs** place a `grid-row: foo` / `grid-area: foo` item at the foo region via the line-name lookups, even with no `grid-template-areas` entry. **PR #215 review** hardened it: per §8.3 the **forward implicit-line assumption** now resolves a positive occurrence / span shortfall through the implicit lines past the explicit grid (so `foo 2` with one `foo` line, and `1 / span foo 2` with too few `foo` lines, extend the grid instead of falling back to auto — capped at `MaxImplicitTracksPerAxis`); a **negative integer start is normalized** against the explicit-grid track count before the span math (so `-3 / span foo` is a 1-track span, not a 6-track one); the per-line **`(name, line)` set is deduplicated** per §8.1 (so a name repeated on one physical line — e.g. `repeat(2, [foo] 100px [foo])` — counts once); and the out-of-range fallback uses a `long` for its diagnostic so a hostile `foo -2147483648` can't overflow `Math.Abs`. Residual (still falls back): `span <custom-ident>` on the START / auto-start edge (needs the auto-placement span algorithm), negative-occurrence start-side implicit fill, + the explicit `GridAreaRect` registration in `NameToRect` (the placement works without it). Grids that don't use these forms are byte-identical.
- **`flex-direction: column` cross-axis honors `direction: rtl`** (CSS Flexbox L1 §3.1 / CSS Box Alignment L3, `flex-layouter-features` narrowed) — a column flex container's cross axis is the inline axis, so `direction: rtl` permutes its cross-start/cross-end (inline-start = right). ONE orientation flag (`isCrossAxisReversed = isWrapReverse ^ isColumnRtl`) drives BOTH the per-line stacking (`CrossAxisFlow`, fed the line's effective cross extent so a full-extent line reverses to a no-op) AND the per-item `align-items` / `align-self` anchor (`ComputeAlignItemsPlacement`). So a multi-line column-rtl `wrap` stacks its lines from the physical right, a single non-stretched `align-content: flex-start` line packs at the right, a `wrap-reverse` column-rtl cancels back, and `align-items: flex-start` right-anchors / `flex-end` left-anchors — matching browsers. The `self-start` / `self-end` logical keywords resolve against the **item's own** `direction` (an LTR child in an RTL column anchors at its own start), via a preserved `IsSelfRelative` flag — distinct from container-relative `flex-start`/`start`. Residual: `start`/`end` vs `flex-start`/`flex-end` divergence under `wrap-reverse` (a pre-existing logical-keyword gap) + vertical writing modes. Row and LTR flex are byte-identical (the flip only engages for column + rtl; a same-direction item's `self-*` is identical to `flex-*`).
- **`break-before` / `break-after: recto | verso | all` now parse** (CSS Fragmentation L3 §3.1, `fragmentation-control-residuals` narrowed) — AngleSharp.Css 1.0.0-beta.144 keeps `page` / `left` / `right` / `column` / `region` / `avoid*` / `auto` but DROPS the `recto` / `verso` / `all` forced-break values (its grammar predates them), so those declarations never reached the cascade even though the keyword table mapped them and the break reader honored them. A `CssPreprocessor` value-gated recovery (`IsRectoVersoAllBreakValue`) now emits the three dropped values verbatim, so `break-before: recto` (etc.) reaches the cascade and forces a page break. Residual: `recto` / `verso` blank-page side PARITY (landing on a left/right page), the optimizing resolver driving production so `*:avoid` bites, and per-paragraph orphans/widows at line-break time. Documents without these three values are byte-identical (the other break values were never recovered).
- **The `dir` HTML attribute maps to `direction`** (HTML §3.2.6.4, `rtl-fragment-reversal` narrowed) — `<div dir="rtl">` / `dir="ltr"` now set the computed `direction` (so the initial `text-align: start` right-aligns RTL content), via `BoxBuilder.ApplyDirAttribute` as a UA-origin presentational hint: applied after inheritance (overrides an inherited direction) but before author declarations (a CSS `direction` still wins); `dir="auto"` keeps the inherited direction (the bidi first-strong heuristic remains a residual). A CSS-wide `direction` declaration (`inherit`/`unset`/`initial`/`revert`/`revert-layer`) is resolved by the cascade — the `dir` hint steps aside for it — and the attribute value is whitespace-trimmed (`dir=" rtl "`). The deferral's remaining parts — UAX-9 fragment-level slice reversal and the margin-box base direction — are closed in the next entry. Documents without a `dir` attribute are byte-identical.
- **Bidi text paints in visual order — UAX #9 rule L2 slice reordering** (`rtl-fragment-reversal` CLOSED) — a line's per-run slices had correct bidi levels but were always walked in document (logical) order, so an RTL run — or an RTL span embedded in LTR text (the common "English with a Hebrew/Arabic name", or numbers in an RTL paragraph) — painted left-to-right. `LineBuilder.Wrap` now reorders each line's slices by their shaped run's embedding LEVEL (`ReorderSlicesByLevelL2`): from the highest level down to the lowest odd level, it reverses every contiguous run of slices at that level or higher. This is exact UAX #9 L2 at run granularity — an RTL base reverses the whole line (embedded LTR double-reverses back to logical), while embedded RTL inside an LTR base reverses only the RTL spans (and Latin runs in an RTL paragraph, being level 2, correctly stay in document order). The level is taken from each `ShapedRun.Source.BidiLevel`, which `Itemize` already resolved (folding in the base direction AND the `Auto` first-strong rule), so no base-direction flag is repassed to `Wrap`. The glyph order WITHIN each slice is left as HarfBuzz shaped it; `TotalAdvance` (the order-independent sum) is unchanged. `PageMarginBoxPainter` reads its content's own `direction` (was hardcoded LTR) at every inline-layout site. A line with no odd level (pure LTR) is left untouched → byte-identical.
- **Mixed-script paragraphs shape per script** (UAX #24, `uax-24-script-detection` — detection mechanism shipped, NARROWED to a table-completeness residual) — `LineBuilder.Itemize` previously produced runs split only by bidi level + source style, so a same-direction script change (e.g. Latin + Han, both LTR) shared one HarfBuzz pass with the caller's single uniform script (the production caller hardcodes `Latn`) and mis-shaped the non-Latin part. A new script table (`NetPdf.Text.Bidi.UnicodeScripts`, sorted range lookup) now drives a script-change boundary in `Itemize`: each itemized run carries its ISO 15924 script tag, and `LineBuilder.Shape` shapes it with that script (`run.ScriptIso15924 ?? uniform`) so Arabic / Han / Hangul / Indic runs get their own OpenType feature set. `Common`/`Inherited` codepoints (digits, punctuation, spaces, combining marks) extend the surrounding run and a leading Common prefix adopts the following script (UAX #24 §5.1). The table is a **block-based APPROXIMATION** of the exact UAX #24 Script property covering the ~30 major scripts — uncovered assigned ranges (CJK Ext C+, Arabic Extended-A, rare scripts) resolve to `Common` (the surrounding/uniform script); the exact UCD `Scripts.txt`-derived table remains the tracked residual (`deferrals.md#uax-24-script-detection`, with tests pinning the uncovered-range behavior). A single-script paragraph whose content matches the caller's uniform script is byte-identical.
- **`word-break: keep-all` keeps CJK runs together** (CSS Text §5.2 / UAX #14 LB30b, `word-break-keep-all-cjk` CLOSED) — `keep-all` previously behaved like `normal` (no break suppression) and a per-run `keep-all` ↔ `normal`/`break-all` mismatch threw `NotSupportedException` from `InlineLayouter.LayoutPerRun`. `LineBuilder.Wrap` now demotes the implicit inter-character soft-wrap opportunities between two East-Asian "letter units" (UAX #14 line-break classes ID / CJ / H2 / H3 / JL / JV / JT) from `Allowed` to `Prohibited`, so CJK text breaks only at spaces / explicit opportunities — applied uniformly or per source run (so a keep-all run mixed with others no longer throws; it flows through the per-run policy array). Latin / non-CJK content is byte-identical (the suppression only fires between CJK codepoints under keep-all).
- **`repeat(auto-fit, …)` collapses empty grid tracks** (CSS Grid L1 §7.2.3.1, `grid-auto-fit-collapse-empty-tracks-deferral` CLOSED) — `auto-fit` previously expanded IDENTICALLY to `auto-fill` (the empty-track collapse was approximated + flagged by `LAYOUT-GRID-AUTO-FIT-APPROXIMATED-001`). After placement, the sizing pass now collapses every auto-fit-derived track that holds no item: its base / growth-limit go to 0, it leaves the fr pool, and the gutters on either side merge (a collapse-aware free-space gutter count + a "gutter only between visible tracks" position pass). So `repeat(auto-fit, minmax(100px, 1fr))` with fewer items than the derived track count makes the FILLED tracks absorb the freed space (e.g. 2 items in a 4-track-deriving 400px grid → two 200px columns) where `auto-fill` would keep them narrow with trailing empty space — the canonical responsive-grid difference. Auto-fit tracks are tagged through `ExpandTrackList` → `TrackSizingInfo.IsAutoFitDerived`; the approximation diagnostic is retired (a regression test asserts it is no longer emitted). Non-auto-fit grids have no collapsible track, so they are byte-identical (the position pass is bit-identical when nothing collapses).
- **`grid-row/column: auto / <line>` no longer emits a spurious placement-approximated diagnostic** (CSS Grid L1 §8.5, `grid-reverse-auto-placement-deferral` closed) — an item with an auto start and a definite end line has an implicit span of 1 (a span > 1 requires a `span` keyword, which routes to auto/span placement), so it correctly occupies the single cell ending at that line (`start = end − 1`). The placement was already correct; the deferral's premise (that a `span` could reach the auto-start-definite-end branch and need the §8.5 backward search) was inaccurate, so the `LAYOUT-GRID-PLACEMENT-APPROXIMATED-001` warning it emitted for both the integer- and named-line-end cases was spurious and is removed. The warning still fires for genuinely-approximated placements (unresolved named lines, occurrence syntax). Verified by `F7_auto_start_definite_end_places_single_cell_without_a_diagnostic`.
- **Struck three grid pagination deferrals already resolved end-to-end** — `grid-wrapper-rollback-for-pre-dispatch-deferral` (F1 pre-dispatch row-fit, shipped at both outer + recursive sites), `grid-fragment-extent-emitted-rows-only-deferral` (F2 wrapper-resize to the emitted extent), and `grid-explicit-height-paginate-deferral` (F3 dual-input geometry/budget separation) — were documented `RESOLVED end-to-end` with named regression tests but still listed. Verified the `Cycle5c2a_F1_*` / `Cycle5c2b_F2_*` / `Cycle5c2d_*` / `Cycle5c3_*` suites pass, then removed the entries (their only residuals are already tracked under `recursive-block-continuation-consumed-extent-accounting-deferral` and `grid-fragment-plan-shared-sizing-deferral`). Docs-only.

- **`@page` margin boxes honor a declared `line-height`** (CSS Inline 3 §4.2, `margin-box-line-height` closed) — literal margin-box content (e.g. `@bottom-center { content: "…"; line-height: 2 }`) previously used `font-size × 1.2` for its line pitch regardless of a declared `line-height`. `line-height` now joins `MarginBoxStyle`'s inherited longhand whitelist (so it flows root → `@page` context → margin box) and the painter reads it via `ReadLineHeightPx` (the full `<number>` / `<length>` / `<percentage>` grammar), falling back to `font-size × 1.2` only for `normal` / unset. A deferred font-/viewport-relative or `calc()` line-height (`2em` / `1.5rem` / `5vw` / `calc(1em + 4px)`) resolves at paint time against the content font / root / page box, **in place** so the inline-content line metrics see it too; and a declared `%` line-height is converted to a length at the declaring `@page`/box context's font-size before it inherits (so `@page { font-size:20px; line-height:200% }` gives a margin box a 40px length even at a smaller box font-size). A margin box with no declared `line-height` is byte-identical.
- **`text-align: match-parent` resolves against the parent at computed-value time** (CSS Text 3 §7.1, `text-align-match-parent` closed) — previously approximated as a fixed physical `left`, direction-insensitive. `BoxBuilder.ResolveMatchParentTextAlign` now replaces `match-parent` with the PARENT's `text-align`, resolving the parent's `start`/`end` to physical `left`/`right` against the PARENT's `direction`, during the top-down walk. So a child of a `direction: rtl; text-align: start` parent right-aligns (the parent's start-in-rtl) even when the child itself is `ltr`, and the resolved physical value inherits to descendants. The layout-time reader's `match-parent` branch is now a defensive fallback. Documents without `text-align: match-parent` are byte-identical.
- **Percentage `line-height` inherits as a length computed at the declaring element** (CSS Inline 3 §4.2, `line-height-percentage-inheritance` closed) — a `<percentage>` `line-height` (e.g. `line-height: 150%`) is now resolved to a length (% × the DECLARING element's font-size) in the top-down box-builder walk, before it inherits, so a child with a different font-size keeps the parent's computed length instead of re-resolving the percentage against its own font-size. `BoxBuilder.ResolveDeclaredPercentLineHeightInPlace` converts a declared `Percentage` line-height slot to `LengthPx`; `<number>` (inherits as the number) and `em`/`rem` (folded by `DeferredLengthResolver`) are untouched. The declaring element and same-font-size descendants are byte-identical (same used px, resolved earlier); only inheritance across a font-size change changes (to be correct).
- **`white-space: break-spaces` breaks after every preserved space** (CSS Text L3 §6.4, `white-space-break-spaces` closed) — previously treated identically to `pre-wrap` (wrap only at UAX #14 Allowed positions). The `LineBuilder` flat-build now upgrades each preserved SP/TAB glyph in a `break-spaces` source run to a `Allowed` break-after, so the wrap pass can break between consecutive spaces (not just after the whole space sequence); preserve-mode spaces are never trimmed, so trailing `break-spaces` spaces keep their advance and wrap rather than hang. Gated to `break-spaces` runs → all other white-space modes are byte-identical.

### Fixed — Phase 3 closeout (remaining residual deferrals)

- **Auto-height block backgrounds span their in-flow children** (CSS 2.1 §10.6.3, `auto-height-emit-vs-pagination` closed) — an `auto`-height block-flow container previously emitted only its own chrome (padding + border) as the painted border box, so its background / border / border-radius under-sized when its children were taller. The emitted fragment now spans the content extent (the value already used for the cursor + break accounting), **capped to the page fragment** so a subtree taller than the page paints a page-bounded rectangle — the cap is what keeps multi-page block-flow pagination byte-identical (the earlier uncapped attempt force-overflowed). Pagination, the cursor advance, the break checks, and continuation accounting are unchanged; only the painter's rectangle grows. **CSS 2.2 conformance → 100%** (30/30).
- **A single paragraph taller than a page splits its own lines across pages** (CSS Fragmentation L3 §4, `inline-only-block-line-splitting` narrowed) — a tall inline-only (text-bearing) block previously force-overflowed the page it started on; it now SLICES its wrapped lines across pages. The lines that fit emit on the current page; the remainder resumes via a new `InlineOnlyLineSplitContinuation` (carried in `BlockContinuation.LayouterState`, the grid/multicol resume pattern) — the painter walks `InlineLayout.Lines` by array index, so a page-fragment is the original `Lines[]` sliced to the fitting lines + a fresh block offset (no shaped-run buffers cross the page boundary; the resume page re-runs the deterministic inline pass + re-slices). **orphans / widows** are honored at the cut, read off the paragraph's OWN computed value (per-paragraph). Text-only, chrome-free blocks split; a tall single block with block-axis padding/border or inline atomics still falls back to whole-block force-overflow (the narrowed residual). Non-splitting prose rendering is byte-identical.
- **Multi-page allocation churn dropped from O(n²) to ~O(1)/page** (`multi-page-allocation-churn` closed, exit criterion 8 allocation linearity now met) — a table that fragments across N pages was re-shaped (column split + every cell) ONCE PER PAGE by the subtree-extent pass (a transient `TableLayouter` with no continuation), so per-page transient allocation grew ~O(n) with page count (≈ O(n²) total: 28 → 192 MiB/page over 5 → 39 pages). A new cross-page `TableMeasurementCache` (threaded through `LayoutContext` like `GridMeasureCache`) holds the page-invariant column layout keyed by the table box + content inline size, so the table is fully measured ONCE per conversion and reused by every page (and the first page's dispatch). Per-page allocation is now flat (~5 MiB/page; total at 39 pages dropped ~33×), enforced by a new allocation-slope gate. The cached state is the same deterministic, page-invariant data the cross-page `TableContinuation` already reused, so output is byte-identical (LayoutSnapshots / PaginationGolden / RealDocuments / AOT sha256 unchanged).

### Added — Phase 4 visual parity: shadows + 2D transforms

- **`box-shadow`** (outset) now paints UNDER the background (CSS B&B §7.2): a sharp (blur ≈ 0) layer is a native filled (rounded) rect offset + spread-expanded from the border box (corner radii grown by the spread); a blurred layer rasters through a new Skia bridge (`ShadowRasterizer` — draws the rounded-rect shape, applies a Gaussian blur with σ = blur/2, and places it as an image XObject with an alpha `/SMask`) at 2× resolution, capped at 4096 px/side, emitting `CSS-BOXSHADOW-BLUR-RASTER-001`. Multi-layer lists paint in reverse so the first-listed sits on top. `inset` shadows + unresolvable units (px + absolute units supported; em/rem/% not) are skipped with `CSS-BOXSHADOW-UNSUPPORTED-001` — outset-only first cut.
- **`text-shadow`** draws the shaped glyph run offset in the shadow color UNDER the main text (no re-shaping — the shadow reuses the run's glyph ids). A non-zero blur is painted as a sharp offset (the Gaussian glyph-blur raster is a tracked follow-up) flagged by `CSS-TEXTSHADOW-UNSUPPORTED-001`, which also covers unsupported units. Collected as the box's OWN declared value (inheritance to descendant text is a documented residual).
- **2D `transform`** (`translate`/`scale`/`rotate`/`skew`/`matrix` + axis variants) wraps a box's whole painting — decoration AND text — in a PDF `cm` about `transform-origin` (default 50% 50%), composing the CSS transform with the y-flip so the origin stays fixed and rotation reads correctly. 3D functions flatten to 2D (`translate3d`/`scale3d` keep their 2D part; the rest project to identity) with `CSS-TRANSFORM-3D-UNSUPPORTED-001`; an unparseable value paints untransformed with `CSS-TRANSFORM-UNSUPPORTED-001`. `transform` / `transform-origin` (dropped by AngleSharp.Css) are recovered via `CssPreprocessor`.
- All three are gated — non-shadow / non-transform rendering is byte-identical. Deferred (documented in `deferrals.md`): inset box-shadow, text-shadow inheritance + true glyph blur, faithful 3D-projection + the transformed-element stacking context.

### Added — Phase 4 visual parity: native PDF gradients

- **`background-image: linear-gradient(...)`** now paints as a PDF native axial shading (ISO 32000-2 ShadingType 2) instead of being skipped: a greenfield shading foundation (`PdfDocument.RegisterAxialShading` + a Type-2 / Type-3-stitching color function + `PdfPage.PaintShadingInRect` clip-and-`sh`), a `linear-gradient()` parser (default `to bottom`, `to <side>`/`<corner>`, `<angle>` in deg/grad/rad/turn, percentage stops, function colors), and the painter axis math (CSS Images §3.1 gradient line). Border-radius clips the gradient. A `to <corner>` direction is aspect-ratio correct (the angle is derived from the painted box, not a fixed 45°).
- **`background-image: radial-gradient(...)`** paints as a PDF native radial shading (ShadingType 3), reusing the foundation: shape (`circle`/`ellipse`), the four extent keywords, and `at <position>` (center / sides / percentages, order-independent keyword pairs; a duplicate-axis or misordered position is rejected). FIRST CUT: the ending shape is painted circularly (an `ellipse` is approximated by its scalar extent — exact for a centered gradient on a square box).
- Both are gated — non-gradient rendering is byte-identical. Repeated identical gradients share one color function (and, when coincident, one shading object). A multi-layer background list is rejected (single-function-token guard). Deferred (documented): `box-shadow`, elliptical radial shaping via CTM, `repeating-*` / conic gradients, length-positioned stops, per-stop alpha (soft-mask), gradient `background-clip`/`-origin` insets.

### Fixed — CSS sizing residuals (conformance raised past the `0.7.0-beta` baseline)

- **Grid `fr` tracks subtract the gutters from their distributed free space** (CSS Grid L1 §7.2.3/§11.5) — `grid-template-columns: 1fr 1fr; column-gap: 20px` in a 400px container now sizes each fr track at 190 (= (400-20)/2), not 200; percentage tracks still resolve against the full content area. Grid conformance → **100%** (15/15).
- **Percentage `min-width` / `max-width` / `min-height` / `max-height` resolve against the containing block** (CSS 2.1 §10.4/§10.7), box-sizing-aware, with the indefinite-axis rule (a `%` max against an indefinite base → `none`, a `%` min → `0`). CSS 2.2 conformance → **96.7%** (29/30).
- **Percentage `column-gap` / `row-gap` resolve against the container content box** (CSS Box Alignment L3 §8.3) — the properties are now `<length-percentage>` (column-gap → inline size, row-gap → block size), on flex AND grid containers.

### Added — Flexbox L1 completion (`flex-layouter-features`)

- **`align-items: baseline` / `align-self: baseline`** (CSS Flexbox L1 §8.3 + CSS Box Alignment L3 §6.2) — the three `<baseline-position>` keywords now perform real first-baseline alignment on a ROW container's cross axis (each item is shifted so its first text baseline sits on the line's max baseline; an item with no line box synthesizes its baseline from its cross-end edge per §8.5). Previously they decoded to `stretch`. COLUMN baseline falls back to flex-start (no first baseline on the inline axis without vertical text).
- **`flex-basis: content` / `max-content` / `min-content`** (CSS Flexbox L1 §7.2 + §9.2.3) — intrinsic flex base sizing on the nowrap ROW main axis: the item is measured to its max-content (no wrap pressure) or min-content (maximal wrap pressure) inline extent, fed through the §9.7 flexibility resolution. The keyword (which AngleSharp.Css drops) is recovered through `CssPreprocessor`; the FlexLayouter emission and the BlockLayouter row-flex pre-measure build the base sizes via one shared helper so they stay in lockstep. WRAP rows + `fit-content` remain deferred.
- The `flex` shorthand (§7.4) was already shipped (`FlexShorthandExpander`); a flex-item-level integration test now pins `flex: 1` → grow 1 / shrink 1 / basis 0 end-to-end.

[Unreleased]: https://github.com/raroche/NetPdf/compare/0.7.0-beta...HEAD

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

- **7,157 unit tests** (+3 skipped), 30 layout snapshots, 97 real-document goldens, 4 W3C conformance gates, pagination golden, rendering corpus, PDF validation, AOT/JIT byte-parity, and determinism — all green at **0 warnings** in Release.

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

[0.1.0-alpha]: https://github.com/raroche/NetPdf/compare/0.0.1-phase0...0.1.0-alpha
[0.3.0-alpha]: https://github.com/raroche/NetPdf/compare/0.1.0-alpha...0.3.0-alpha
[0.7.0-beta]: https://github.com/raroche/NetPdf/compare/0.3.0-alpha...0.7.0-beta
