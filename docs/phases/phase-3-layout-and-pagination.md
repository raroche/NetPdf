# Phase 3 — Fragmentainer-Aware Layout + Pagination

**Status:** ⏳ pending (after Phase 2). **The bottleneck phase.**
**Time:** team estimate 16 wk → Claude Opus 4.7 high: **8–12 wk**.
**Tagged release:** `0.7.0-beta` — most business documents render correctly. The first user-useful release.

## Goal

Build the layout engine and the pagination optimizer as **a single interleaved system**, not as separate stages. Layouters consume `BoxTree` (from Phase 2), call into the `BreakResolver` *during* layout to make break decisions, and produce a paginated `FragmentTree` that Phase 4's painter consumes. By end of Phase 3, NetPdf renders production-grade invoices, contracts, statements, reports, and books.

## Why this is the hardest phase

CSS layout is the single most complex problem in front-end software. Block/inline + floats + tables + flexbox + Grid + multicol + absolute/fixed positioning + fragmentation + widows/orphans + repeated headers + page-margin boxes + running elements interact in subtle ways. The W3C test suite has thousands of cases; getting passing rates above 80% is hard, above 95% is years of polishing in browser engines.

**This phase succeeds by being honest about scope.** Grid Level 1 (no subgrid). Sticky positioning post-v1. CSS animations skipped (PDF is static). Container queries parsed but not rendered. The compatibility matrix is the contract.

## Prerequisites

- Phases 0–2 complete; `0.3.0-alpha` tagged.
- Read [phase-1-pdf-writer-and-text.md](phase-1-pdf-writer-and-text.md) — `NetPdf.Text` (HarfBuzz, bidi, line break, hyphenation) is consumed by the inline layouter.
- Read [phase-2-css-engine.md](phase-2-css-engine.md) — `BoxTree` is the input.
- Read [../compatibility-matrix.md](../compatibility-matrix.md) — every "Phase 3" entry is in scope; everything else emits a diagnostic.

## Deliverables

### `NetPdf.Paginate` — the break-resolver service (build first!)

**Critical:** build the paginator *before* the layouters. Layouters consume its interfaces from day one, so define those interfaces first.

- **`FragmentainerContext`** (`src/NetPdf.Paginate/FragmentainerContext.cs`): the per-page mutable state — page number, page box, content-area rect (CSS px), `RemainingHeight`, float manager state, named-strings table, structural counters (`page`, `pages`).
- **`BreakResolver`** (`src/NetPdf.Paginate/BreakResolver.cs`): the layouter-facing API.
  ```csharp
  internal interface IBreakResolver
  {
      BreakDecision ConsiderBreakAt(BlockState state, FragmentainerContext ctx);
      void RegisterCheckpoint(LayoutCheckpoint cp);
      LayoutCheckpoint? GetLastCheckpoint();
  }

  internal readonly record struct BreakDecision(BreakAction Action, double Cost, LayoutCheckpoint? RewindTo);
  internal enum BreakAction { Continue, BreakHere, Rewind }
  ```
- **`CostModel`** (`src/NetPdf.Paginate/CostModel.cs`): assigns penalty for candidate break points. Penalties documented in the plan; defaults in code with constants tunable per-document via `HtmlPdfOptions.Features` (future).
- **Bounded DP optimizer** (`src/NetPdf.Paginate/Optimizer.cs`): Knuth-Plass-style cost minimization with 1–2-page lookahead. Runtime O(n × k²); n = candidates, k = lookahead window. Last-resort fallback to greedy with `PAGINATION-OPTIMIZER-FALLBACK-001` diagnostic.
- **`LayoutCheckpoint`**: snapshot of `FragmentainerContext` + `LayoutContext` enabling rewind. Pooled to avoid allocation in the hot loop.
- **Re-layout loop bound**: max 2 retries per fragmentainer. After 2, commit best-cost result and emit `PAGINATION-FORCED-OVERFLOW-001`.

### `NetPdf.Layout` — the layouters

All layouters share these properties:
1. Take a `LayoutContext ref struct` (containing-block stack, writing mode, available width/height, float state, named strings).
2. Take a `FragmentainerContext` (current page state).
3. Consult `IBreakResolver.ConsiderBreakAt(...)` at every legal break point.
4. Return a layout result that may include a **continuation token** if the box was split.

#### `BlockLayouter`
- CSS 2.1 block-level algorithm: vertical box stack with margin collapsing.
- BFC root detection.
- `intrinsic` width modes: `min-content`, `max-content`, `fit-content`.
- Float interaction (consults `FloatManager`).
- Continuation: a block may produce a `BlockContinuation { ResumeAtChild, RemainingHeight }` token when split across pages.

#### `InlineLayouter` + `LineBuilder`
- Per-block inline call into `LineBuilder`:
  1. **Bidi** (UAX #9 from `NetPdf.Text`) → resolved levels.
  2. **Itemization**: split into runs of (direction, script, font, style).
  3. **Shape** each run via `HbShaper.Shape(...)` from `NetPdf.Text`.
  4. **Break opportunities** via UAX #14 line-break + `hyphens: auto` consulting Liang patterns.
  5. **Measure** each shaped run's advance.
  6. **Wrap** lines, applying `text-align`, `white-space`, `overflow-wrap`, `word-break`, `vertical-align`.
- Output: `LineFragment[]` with absolute glyph positions in CSS px.
- Continuation: lines are atomic; continuation token is `InlineContinuation { ResumeAtTextRun, ResumeAtCluster }`.

#### `FloatManager`
- Tracks left/right floats per BFC.
- Resolves `clear`.
- Per CSS Fragmentation L3 §5: floats that don't fit move to top of next fragmentainer (not propagated from same offset).

#### `TableLayouter`
- **Auto layout** (CSS 2.1) and **fixed layout** (`table-layout: fixed`).
- `border-collapse: collapse` (collapsed-borders model) vs `separate` (separated borders with `border-spacing`).
- Cell merging: `rowspan`, `colspan`.
- **`<thead>` / `<tfoot>` repetition**: when a table fragment crosses a page boundary, the header repeats at top of the next page and the footer (if present) appears above the break. Continuation token: `TableContinuation { RepeatHeader, RepeatFooter, NextRowIndex, ColumnLayoutCache }`.

#### `MulticolLayouter`
- `column-count`, `column-width`, `column-gap`, `column-rule`.
- Content flows left-to-right (or per writing mode) into N columns.
- Multi-col interaction with fragmentation: each column is its own sub-fragmentainer; the outer multicol box is itself fragmented across pages.

#### `FlexLayouter`
- CSS Flexbox L1 (full).
- Algorithm phases (per spec §9):
  1. Determine flex container's main size.
  2. Determine flex item's flex basis.
  3. Calculate free space.
  4. Distribute via `flex-grow` / `flex-shrink`.
  5. Cross-axis alignment: `align-items`, `align-self`, `align-content`.
- Cross-fragment baseline alignment: a flex container is laid out **whole** within its containing block (must measure all items for baseline). The resulting fragment is then page-fragmented by the outer block algorithm.
- Continuation: `FlexContinuation { BaselineState, LineIndex }` for multi-line wrap that splits.

#### `GridLayouter` (Level 1 only)
- CSS Grid L2 spec, but **subgrid is post-v1**.
- Track sizing algorithm (the most complex algorithm in CSS):
  - Track types: `auto`, `fr`, fixed length, `minmax(min, max)`, `fit-content(...)`, `repeat(N, ...)` including `auto-fill` / `auto-fit`.
  - Two passes: intrinsic sizing → flex-track distribution.
- Item placement:
  - Explicit via `grid-row` / `grid-column` / `grid-area`.
  - Auto-placement: sparse + dense packing modes.
- `grid-template-areas` named-area parsing.
- Like flex: laid out whole within its containing block; outer paginator may fragment the result.
- **Read Servo's `layout_2020/grid` and Taffy's grid implementation for understanding**, NOT for transliteration. Per the clean-room policy, leave a one-line note in `GridLayouter.cs` if you read another implementation.

#### `AbsoluteLayouter`
- `position: absolute` and `position: fixed`.
- Containing block resolution: nearest positioned ancestor (or initial containing block).
- `position: fixed` is special — the box paints **into every page** (or every page-margin box, depending on stacking).

### Page-margin boxes & running elements

- **`@page` rule resolution** (`src/NetPdf.Paginate/PagedMedia/`): handles `@page :first` / `:left` / `:right` / `:blank`, named pages.
- **16 page-margin boxes**: `@top-left-corner`, `@top-left`, `@top-center`, `@top-right`, `@top-right-corner`, `@right-top`, `@right-middle`, `@right-bottom`, `@bottom-right-corner`, `@bottom-right`, `@bottom-center`, `@bottom-left`, `@bottom-left-corner`, `@left-bottom`, `@left-middle`, `@left-top`. Each lays out as its own mini-block formatting context.
- **`string()`** generated content: a named string is set when an element with `string-set: name content` is encountered; `content: string(name)` in a page-margin box pulls the current value.
- **`element()`** generated content (CSS GCPM L3): `position: running(name)` removes an element from normal flow; `content: element(name)` in a page-margin box renders it. **This is the keystone feature that the Anvil sample exercises.**
- **`counter(page)`** / `counter(pages)`: page counter via `content: counter(...)` in any pseudo-element; `page` is current, `pages` is total.

### Diagnostics integration

- Every `CSS-*` rendering-related code from `docs/diagnostics-codes.md` emits from this phase's layouters as appropriate.
- Pagination-specific codes: `PAGINATION-OPTIMIZER-FALLBACK-001`, `PAGINATION-FORCED-OVERFLOW-001`.

## Spec references

| Topic | Source |
|---|---|
| CSS 2.1 (block, inline, table, float) | https://www.w3.org/TR/CSS21/ |
| CSS Box L4 | https://www.w3.org/TR/css-box-4/ |
| CSS Inline L3 | https://www.w3.org/TR/css-inline-3/ |
| CSS Tables L3 | https://www.w3.org/TR/css-tables-3/ |
| CSS Multi-col L1 | https://www.w3.org/TR/css-multicol-1/ |
| CSS Flexbox L1 | https://www.w3.org/TR/css-flexbox-1/ |
| CSS Grid L2 | https://www.w3.org/TR/css-grid-2/ |
| CSS Positioned Layout L3 | https://www.w3.org/TR/css-position-3/ |
| CSS Fragmentation L3 | https://www.w3.org/TR/css-break-3/ |
| CSS Paged Media L3 | https://www.w3.org/TR/css-page-3/ |
| CSS GCPM L3 (running elements, page floats) | https://www.w3.org/TR/css-gcpm-3/ |
| CSS Writing Modes L3 | https://www.w3.org/TR/css-writing-modes-3/ |
| W3C CSS test suite | https://wpt.fyi/results/css |

## Work breakdown (ordered)

| # | Task | Mini-est. | Depends on |
|---|---|---|---|
| 1 | `FragmentainerContext` + `IBreakResolver` interfaces | 2 d | — |
| 2 | `LayoutCheckpoint` + checkpoint pool | 2 d | 1 |
| 3 | `CostModel` with documented penalties | 2 d | 1 |
| 4 | Bounded DP optimizer | 4 d | 3 |
| 5 | Re-layout loop with bounded retry | 2 d | 4 |
| 6 | `LayoutContext ref struct` | 1 d | — |
| 7 | `BlockLayouter` (margin collapsing, BFC, sizing) | 5 d | 1, 6 |
| 8 | `FloatManager` + `clear` | 4 d | 7 |
| 9 | `LineBuilder` (bidi → shape → break → wrap) | 6 d | 7 |
| 10 | `InlineLayouter` calling `LineBuilder` | 3 d | 9 |
| 11 | Block + inline integration; widows/orphans | 3 d | 7, 10 |
| 12 | `TableLayouter` auto + fixed + collapse + span | 6 d | 7 |
| 13 | Table `<thead>`/`<tfoot>` repetition across pages | 3 d | 12 |
| 14 | `MulticolLayouter` | 3 d | 7 |
| 15 | `FlexLayouter` L1 (single-line) | 5 d | 7 |
| 16 | `FlexLayouter` multi-line wrap + cross-fragment baseline | 4 d | 15 |
| 17 | `GridLayouter` track-sizing algorithm | 8 d | 7 |
| 18 | `GridLayouter` placement + dense packing + areas | 5 d | 17 |
| 19 | `AbsoluteLayouter` for `position: absolute` | 3 d | 7 |
| 20 | `position: fixed` repetition per page | 2 d | 19 |
| 21 | `@page` rule + 16 page-margin boxes | 5 d | 7 |
| 22 | `string()` + `string-set` | 2 d | 21 |
| 23 | `position: running()` + `content: element()` | 3 d | 21 |
| 24 | `counter(page)` / `counter(pages)` | 1 d | 21 |
| 25 | Diagnostics emission for pagination edge cases | 2 d | 4, 5 |
| 26 | W3C CSS test runner integration | 3 d | all-of-above |
| 27 | Pagination-golden snapshot infrastructure | 2 d | 26 |
| 28 | Render the 4 invoice corpus files end-to-end | 5 d | all-of-above |
| 29 | Performance tuning to hit 1-page invoice ≤ 200 ms p50 | 4 d | 28 |
| 30 | Tag `0.7.0-beta` + CHANGELOG | 0.5 d | 28 |

**Total: ~100 days. With Claude Opus 4.7 high + daily Roland review: 8–12 calendar weeks.**

## Implementation notes

### Build the paginator first
The single most important sequencing rule for this phase. If `BlockLayouter` is built before `IBreakResolver` exists, every layouter has to be retrofitted later — guaranteed re-work. Define the interfaces in week 1, even if the bodies are stubs that always return `BreakAction.Continue`.

### Continuation tokens
Every layouter that can be split must produce a continuation token. The shape:
```csharp
internal abstract record LayoutContinuation;
internal sealed record BlockContinuation(int ResumeAtChild, double ConsumedHeight) : LayoutContinuation;
internal sealed record TableContinuation(bool RepeatHead, bool RepeatFoot, int NextRowIndex, ColumnLayout ColumnCache) : LayoutContinuation;
internal sealed record FlexContinuation(BaselineSnapshot Baselines, int LineIndex) : LayoutContinuation;
internal sealed record InlineContinuation(int RunIndex, int ClusterIndex) : LayoutContinuation;
```
On rewind, the continuation token plus the box pointer is enough to resume at the exact next position.

### Re-layout loop discipline
```
attempt = 0
while attempt < MAX_RETRIES (= 2):
    checkpoint = saveContext()
    result = layouter.Layout(box, ctx)
    decision = breakResolver.ConsiderBreakAt(result, ctx)
    if decision.Action == Continue: return result
    if decision.Action == BreakHere: emit fragment + new ctx; return continuation
    if decision.Action == Rewind:
        restoreContext(decision.RewindTo)
        attempt++
emit PAGINATION-FORCED-OVERFLOW-001; commit best-cost result
```

### Cost model defaults
| Penalty | Score |
|---|---|
| Break inside `break-inside: avoid` | +∞ (until last-resort) |
| Heading stranded at page bottom (heading + 0 lines following) | +1000 |
| Orphan: 1 line at page bottom while widows constraint requires 2+ | +500 |
| Widow: 1 line at page top while widows constraint requires 2+ | +500 |
| Splitting a table row mid-cell | +300 |
| Splitting a flex/grid line | +400 |
| Large blank trailing area on page (> 30% of page height) | +200 |
| Section boundary break | -100 (reward) |

Tune these against the rendering corpus during week 12.

### Performance budget
Per the plan's gates:
- 3-page invoice ≤ 200 ms p50.
- 20-page report ≤ 1.5 s p50.
- Linear memory growth.

Hot paths to keep allocation-free:
- `LineBuilder.Wrap` (called per block per fragmentainer).
- Cascade-driven property lookup (cache hit rate matters).
- Glyph buffer construction (HarfBuzz returns can be ArrayPool-backed).
- Float manager line scanning.

Per-page parallelism is **not** safe in layout (cumulative state for named strings and counters). It's safe in paint (Phase 4); use it there.

### CSS Grid pragma
Read these for understanding; do NOT transliterate:
- Servo `layout_2020/grid` — modular and well-commented.
- Taffy crate's grid module — pedagogical.
- Chromium's NGGridLayoutAlgorithm — production-grade reference.

Mark `src/NetPdf.Layout/Grid/` files with a one-line note like:
```csharp
// Track sizing algorithm understanding informed by reading Servo layout_2020/grid
// and Taffy grid module; no code copied. See ../docs/clean-room-policy.md.
```

## Test plan

| Component | Test | Location |
|---|---|---|
| `BreakResolver` | Unit | Cost model assignment matrix; DP optimizer with hand-built candidate sets. |
| `LayoutCheckpoint` | Unit | Save/restore round-trip for various contexts. |
| `BlockLayouter` | Unit + Snapshot | CSS 2.1 §10 examples; 30 margin-collapsing cases; BFC root cases. |
| `FloatManager` | Unit | 20 float/clear cases from CSS 2.1 §9.5. |
| `LineBuilder` | Unit | Latin, Arabic (RTL), Hindi (Indic), Hebrew (RTL), Chinese (CJK), Thai (no-spaces). Mixed bidi. |
| `InlineLayouter` | Snapshot | Vertical-align variants, line-height, white-space modes. |
| `TableLayouter` | Snapshot | auto + fixed + collapse + span; thead/tfoot repetition across pages. |
| `MulticolLayouter` | Snapshot | column-count + column-width interactions. |
| `FlexLayouter` | W3C subset | Run flex spec examples + WPT flex tests. Track pass-rate. |
| `GridLayouter` | W3C subset | Run grid spec examples + WPT grid tests. Track pass-rate. |
| `AbsoluteLayouter` | Snapshot | Various positioned ancestors. |
| `position: fixed` | Integration | Verify repetition on every page. |
| `@page` margin boxes | Integration | All 16 boxes with content. |
| `string()` / `element()` | Integration | The Anvil corpus sample (#04) is the canonical test. |
| `counter(page)` | Integration | "Page 1 of 5" format on a 5-page document. |
| W3C CSS test suite | Conformance | `tests/NetPdf.W3cConformance/` — pass-rate published in README. |
| Pagination golden | Snapshot | `tests/NetPdf.PaginationGolden/` — input HTML → expected break sequence. |
| Real document corpus | End-to-end | `tests/NetPdf.RealDocuments/Corpus/Invoices/*.html` — render to PDF, structure-validate via PDFium. |
| Performance | Benchmark | 3-page invoice ≤ 200 ms p50; 20-page report ≤ 1.5 s p50. |

## Exit criteria

Phase 3 is complete when:

1. ✅ All 4 invoice corpus files render to a valid PDF (visual fidelity polish lands in Phase 4).
2. ✅ The Anvil sample (`04-anvil-running-elements.html`) renders with the footer + "Page N of M" appearing on every page.
3. ✅ W3C CSS 2.2 layout test pass-rate ≥ 90%.
4. ✅ W3C Flexbox test pass-rate ≥ 85%.
5. ✅ W3C Grid Level 1 test pass-rate ≥ 70% (Grid is genuinely hard; raise the bar in v1.x).
6. ✅ W3C Fragmentation test pass-rate ≥ 80%.
7. ✅ Performance: 3-page invoice ≤ 200 ms p50; 20-page report ≤ 1.5 s p50.
8. ✅ Memory: linear growth with page count.
9. ✅ AOT smoke test still passes.
10. ✅ Determinism: byte-equal output for byte-equal input.
11. ✅ CHANGELOG updated, `0.7.0-beta` tagged.

## Common pitfalls

- **Building layouters before the paginator.** Forces retrofit. Build interfaces first.
- **Margin collapsing rules are subtle.** CSS 2.1 §8.3.1 has 4 sub-rules with exceptions. Test every one.
- **BFC roots.** `overflow: hidden`, `display: flow-root`, `float`, `position: absolute/fixed`, `display: inline-block`, flex/grid items all establish BFCs. Easy to miss one.
- **Bidi reordering interacts with shaping.** Shape *after* bidi resolves directional runs.
- **Table column sizing is two-pass.** Auto layout requires measuring all cells before sizing columns. Cache aggressively.
- **`<thead>` repetition across pages — easy to break determinism.** Make sure the repeated header fragment is byte-identical to the original.
- **Flex baseline alignment across fragments.** A flex container is laid out whole, but the resulting fragment may split. Baseline state must be preserved in `FlexContinuation`.
- **Grid `auto-fill` vs `auto-fit`.** They behave identically except when there are no items and tracks would collapse — `auto-fit` collapses, `auto-fill` doesn't. Test both.
- **Grid intrinsic sizing.** `min-content` / `max-content` in track functions trigger a measurement pass. Not optional.
- **`position: fixed` + transforms.** A `transform` on any ancestor establishes a containing block that captures `fixed` positioning. Easy to miss.
- **Page-margin boxes have their own context.** Don't reuse the main layout context — fresh context per box.
- **Running elements don't paint in their declared position.** They paint in the page-margin box that references them via `content: element(name)`. The original position is empty.
- **Counter scope.** `counter-reset` creates a counter scoped to the element; `counter()` reads the current value visible at that point. Page counters (`page`, `pages`) are special — implicit on `:root`.
- **DP optimizer worst case.** A document with no `break-inside: avoid` and 1000 lines has a candidate set of ~1000. Lookahead window of 2 keeps it bounded; lookahead of 5+ blows up. Don't make it tunable — keep it ≤ 2.
- **Re-layout loop infinite recursion.** Bound to 2 retries. Always.

## Hand-off to Phase 4

State of the repo at end of Phase 3:
- HTML+CSS → paginated PDF works for the supported subset.
- 4 invoice corpus files render correctly (visual quality is "passable, not yet pretty").
- Pagination decisions are deterministic and documented via golden snapshots.
- Performance hits the 200 ms / 1.5 s targets.
- Filters, complex gradients, raster fallback, SVG are still rough.

Phase 4 polishes the painter: gradients (linear/radial native, conic raster), shadows, filters, transforms, image pipeline, SVG. The visual-regression harness against pinned-Chrome reference PNGs locks in the fidelity.
