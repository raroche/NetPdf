# NetPdf — Progress Status

> **Current state (2026-06-20):** Phase 3's layout + pagination engine drives multi-page rendering for tables, flex, grid, multicol, prose, empty/explicit-height blocks. **PR 7 (table/grid/float hardening) just landed:** table intra-cell row splitting (a tall row's stacked-block cell content breaks across pages — task 17), grid cross-page row-extent memo (task 18), and float content atomicity (nested grid/flex in floats stop truncating — task 19). What's left to *finish* Phase 3: (a) W3C conformance **measurement** (PR 1, still awaits the A/B decision), (b) remaining hardening residuals (nested table/multicol in floats, recursive consumed-extent accounting, `multi-page-allocation-churn` — all latent/documented), (c) the `0.7.0-beta` release. Perf/memory exit criteria 7–8 are layout-pipeline smoke-gated (PR 5).
>
> Last merged: PR [#200](https://github.com/raroche/NetPdf/pull/200) (task 16: prose pagination). This branch `phase3-table-grid-float-hardening`: **tasks 17–19** (3 commits, one per task). `git log --oneline -1` shows the exact commit.
>
> This file was consolidated from a 1.1 MB chronological log on 2026-06-18; the full per-subtask history is archived in [docs/progress-archive.md](docs/progress-archive.md). **Keep this file compact** — roll the roadmap as each PR lands; don't grow a blow-by-blow log here.

## Status at a glance

| Phase | Scope | State |
|---|---|---|
| 0 | Legal & architecture lock | ✅ Complete |
| 1 | PDF writer + text foundation | ✅ Complete |
| 2 | CSS engine + DOM pipeline | ✅ Complete |
| 3 | Fragmentainer-aware layout + pagination | 🚧 **Engine done — finishing validation + release** |
| 4 | Visual parity (gradients, shadows, filters, SVG) | ⏸️ Not started |
| 5 | Packaging + release | 🔵 Interleaved — layout→PDF wiring done |

**Gates (all green, 2026-06-20):** 7133 unit / 4 skip (+ the 3 perf/memory gates) · 30 LayoutSnapshots · 97 RealDocuments · W3cConformance (smoke only) · PaginationGolden · RenderingCorpus · 0-warning Release · AOT/JIT parity · determinism.

## Phase 3 — what's shipped (consolidated)

- **Pagination engine (`NetPdf.Paginate`)** — break resolver, documented cost model, bounded-DP optimizer (≤2-page lookahead), continuation tokens, checkpoint/rewind, bounded-retry coordinator.
- **Layouters (`NetPdf.Layout`)** — Block (margin-collapse, BFC, min/max/fit-content, floats), Inline + LineBuilder (UAX#9 bidi, HarfBuzz shaping, UAX#14 breaking, wrap, white-space, `text-align` incl. justify/justify-all, full `vertical-align`), FloatManager + `clear`, Table (auto/fixed, collapse/separate, row/colspan, `<thead>`/`<tfoot>` repeat), Multicol, Flex L1 (single + multi-line + column split + item content), Grid L1 (track sizing, placement, dense, areas, implicit rows), Absolute + `position: fixed`.
- **Multi-page driver** (cycles 0–8, PRs [#175](https://github.com/raroche/NetPdf/pull/175)–[#179](https://github.com/raroche/NetPdf/pull/179)) — nested-container fragmentation, the page-emitting driver loop, per-page counters, cross-page running content, per-page `@page :first/:left/:right/:blank`, named pages, font-dedup across pages.
- **Paged media + generated content** — `@page` rules + the 16 margin boxes (style/border/padding/background/border-radius), `string()`/`string-set` (incl. `content()`), `element()`/`position: running()` (own font/colour/decoration), `counter(page)`/`counter(pages)` with counter styles.
- **Paint (Phase-5-interleaved)** — TextPainter (shaping → subset → embed), FragmentPainter (background-color/-image, borders, outline, border-radius, tiling patterns), image pipeline (`<img>` + `background-*`, `object-fit`/`-position`, data: URIs, Skia raster fallback).
- **Cross-cutting** — determinism gated, AOT/JIT parity gated, 0-warning Release, banned-dependency analyzer.

## Phase 3 — exit-criteria status

Phase 3 is "complete" per [phase-3 §Exit criteria](docs/phases/phase-3-layout-and-pagination.md) when all 11 hold:

| # | Criterion | Status |
|---|---|---|
| 1 | 4 invoice corpus files render to a valid PDF | ✅ |
| 2 | Anvil sample: footer + "Page N of M" on every page | ✅ (multi-page + counters live) |
| 3 | W3C CSS 2.2 layout pass-rate ≥ 90% | ⚠️ **not measured** (harness is a smoke stub) |
| 4 | W3C Flexbox pass-rate ≥ 85% | ⚠️ **not measured** |
| 5 | W3C Grid L1 pass-rate ≥ 70% | ⚠️ **not measured** |
| 6 | W3C Fragmentation pass-rate ≥ 80% | ⚠️ **not measured** |
| 7 | Perf: 3-pg ≤ 200 ms, 20-pg ≤ 1.5 s p50 | 🟡 **layout-pipeline smoke-gated** (`PerformanceGateTests`: 3-pg ~42 ms, 22-pg ~400 ms — synthetic fonts + table content). The FULL-pipeline target (tables + **images + web fonts**, docs/design/performance.md) is the BenchmarkDotNet flow, not yet a build gate. |
| 8 | Memory linear with page count | 🟡 **partial** — RETAINED heap flat (gated); ALLOCATION linearity NOT met: multi-page churn is super-linear (`multi-page-allocation-churn` — the `[MemoryDiagnoser]` standard would flag it). |
| 9 | AOT smoke passes | ✅ |
| 10 | Determinism | ✅ |
| 11 | CHANGELOG + `0.7.0-beta` tagged | ❌ |

**Bottom line:** the big multi-page gap is closed — **prose now paginates** (task 16, block-granularity). Remaining: line-level paragraph splitting (`inline-only-block-line-splitting`, niche), super-linear allocation churn on long docs (`multi-page-allocation-churn`), conformance measurement, and table/grid hardening. The critical path is now **conformance measurement → hardening → release.**

## Phase 3 — remaining-work roadmap

Worked as **3-task PRs** (complete 3 → review → merge → next 3), in order. **PR 1 is the recommended next.** PRs 2–6 are firmer once conformance measurement (PR 1) shows which feature gaps actually move the pass-rates — treat them as a structured pool, reprioritized by findings.

### ▶ PR 1 — Conformance measurement  [criteria 3–6] — DO NEXT
1. **WPT harness** — replace the `NetPdf.W3cConformance` smoke stub with a vendored-WPT loader that renders HTML→PDF and asserts (assertion-/reftest-based); start with a curated CSS 2.2 layout subset.
2. **CSS 2.2 + Fragmentation pass-rates** — wire those two subsets, compute pass-rates, gate ≥ 90% / ≥ 80%.
3. **Flexbox + Grid pass-rates** — wire those subsets, gate ≥ 85% / ≥ 70%, publish the four numbers in the README.

### PR 2 — Direction / bidi pipeline  [feature; several deferrals block on this]
4. ✅ A shared `direction` resolution pipeline — `DirectionStyleExtensions` (`ReadDirection`/`IsRtl`/`ReadParagraphDirection`); `direction` registered (inherited, `ltr`/`rtl`); bidi base direction now CSS-driven at the inline-layout seam. Writing-mode stays horizontal-tb (the seam composes vertical modes later).
5. ✅ RTL `text-align` start/end swap (`ReadInlineAlignFactor` direction-aware — `start`→right in RTL) + RTL inline-atomic alignment (atomic shifts to the right edge). LTR output byte-identical.
6. ✅ RTL flex main-axis flip (`flex-direction: row` under `direction: rtl`) — FlexLayouter XORs its reverse flag via `IsRtl` (row+rtl ≡ row-reverse+ltr); LTR byte-identical. *Residual direction gaps* (see `rtl-fragment-reversal`): UAX #9 L2 slice reversal, `dir` HTML attribute → `direction`, margin-box base direction, flex COLUMN cross-axis RTL.

### PR 3 — Inline-text polish  [feature]
7. ✅ Line-edge `vertical-align` line growth — a tall `top/bottom/middle/text-*` run now GROWS its line (`InlineVerticalAlign.TextLineEdgeGrowth`); the painter follows via the shared per-line metrics.
8. ✅ `line-height` cascade wiring — `LineHeightResolver` + `ReadLineHeightPx` resolve the full `normal | <number> | <length> | <percentage>` grammar (was UNWIRED → silently font-size × 1.2). Residual: `%` inherit-as-length (`line-height-percentage-inheritance` deferral).
9. ✅ inline-block per-run baseline metrics (`BufferingMeasureSink.DeepestLastLineRunStyle` — deepest last-line run drives the descent) + justify-all on internal `<br>` lines (lifts the §7.3 forced-break exception under justify-all).

### PR 4 — Paged-media completion  [feature]
10. ✅ Running-element nested **block** layout — already rendered via the segment-style + container-bands cycles (stacked lines per block child + wrapping + per-block own-style + decorated container bands); confirmed + deferral corrected. Residual: inline-level styling WITHIN a leaf block.
11. ✅ `string(name, start)` / `first-except` (the page entry value; first-except empty when first == start) + compound `@page` selectors (`chapter:first` etc. — already live in the multi-page path; stale roadmap item). element() start/first-except stays deferred.
12. ✅ Page-margin box overflow + container-relative units — CONFIRMED done: overflow (vertical line-granularity truncation + horizontal glyph clip-path + `overflow:visible` opt-out + `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001`) and `%`/`em`/`vw`/`calc()` resolution are implemented + tested; container units (`cqw`/…) are a tracked **post-v1 (v1.4)** deferral, already diagnosed + dropped + tested. Residual: `margin-box-line-height` (deferral), border-radius `calc()` (minor), flex COLUMN cross-axis RTL.

### PR 5 — Perf + memory gates  [criteria 7–8] ✅ DONE
13. ✅ 3-page invoice ≤ 200 ms p50 — enforced `PerformanceGateTests` (~42 ms, synthetic-font table invoice).
14. ✅ 20-page report ≤ 1.5 s p50 — enforced gate (~400 ms, 22-page tabular report; the live multi-page path).
15. ✅ Retained-heap gate — heap flat across page count (criterion 8 PARTIAL: retained footprint linear; ALLOCATION scaling is super-linear — `multi-page-allocation-churn`, the `[MemoryDiagnoser]` standard would flag it; a slope gate is large-doc hardening).

### PR 6 — PROSE PAGINATION  ✅ DONE
16. ✅ **Inline-only block pagination** (block-granularity) — the recursion's inline-only branch now consults the break resolver, so a text block whose margin-box overflows the page breaks WHOLE to the next page (`<p>×200` paginates; no content loss/duplication). Guards a real content extent (`InlineOnlyBreakMinExtentPx`) so a ZERO-extent anonymous block (flex/grid content past the page edge) doesn't spuriously break — the regression that blocked the first cut, root-caused (both triggers were `chunk == 0` at `start > pageBlockSize`). Residual: line-level paragraph splitting / orphans+widows (`inline-only-block-line-splitting`). Full unit suite + snapshots/golden/real-docs all green.

### PR 7 — Pagination / table / grid hardening  [feature] ✅ DONE
17. ✅ **Table intra-cell row splitting** (block-granularity) — a body row whose cell stacks block children taller than the page breaks WITHIN itself across pages (`TableContinuation.RowSplitOffset`; cells measure full natural height via `SuppressBlockPagination`; split-aware dry-run propagates the continuation through the production `BlockLayouter` path). Tight scope: no footers/bottom-captions/rowspan-origin; a single atomic block still force-overflows (`inline-only-block-line-splitting`).
18. ✅ **Grid cross-page row-extent memo** + corrected the deferral premise — the 3 `GridSizing.Resolve` sites take GENUINELY different inputs (indefinite-block site 1 vs definite sites 2/3; probe lacks measurers), so a naive 3→1 share is INCORRECT for `fr` grids; the expensive shaping was already shared. Landed the page-invariant site-1 §11 memo (`GridMeasurementCache.RowExtentSum`) so resume pages skip it; site-2+3 collapse needs a reftest sweep (stays deferred).
19. ✅ **Float content atomicity** — nested grid + flex in a float emit atomically (lossless) instead of paginating-then-truncating (floats don't fragment yet; `_inAtomicFloatSubtree`). Residual (all `not-started`/`approximated`, documented): nested table+multicol in floats still truncate (page-budget-coupled wrapper sizing); recursive consumed-extent accounting + `multi-page-allocation-churn` are LATENT (no visible impact).

### PR 8 — Release  [criterion 11]
20. **Deferral audit** — reconcile `deferrals.md` / `compatibility-matrix.md` with live state; close stale entries (especially the grid residuals, several of which already shipped).
21. CHANGELOG `0.7.0-beta` entry + exit-criteria sign-off.
22. Tag `0.7.0-beta`.

> **Backlog pool** (interleave into the PRs above as conformance findings dictate): flex `align-content: baseline`, flex `%`/`em`/`calc()` item sizing, multicol font-relative `column-width` + balancing cache, grid box-sizing/maximize/perf residuals (**audit first — many may be stale**), `outline` non-solid styles + diagnostic, `page`/`object-position` `@supports` registration, empty-resume-page sentinel cleanup, per-page abspos container geometry. Full inventory: the 2026-06-18 backlog sweep (34 items) is summarized in the conversation that produced this roadmap; ground each task against `deferrals.md` before starting.

## Where to look

- **Next action / live state:** the roadmap above; `git log --oneline -1`; or run `/phase-status`.
- **Deliberately deferred + pickup triggers:** [docs/deferrals.md](docs/deferrals.md).
- **In/out of scope:** [docs/compatibility-matrix.md](docs/compatibility-matrix.md).
- **Phase 3 plan + exit criteria:** [docs/phases/phase-3-layout-and-pagination.md](docs/phases/phase-3-layout-and-pagination.md).
- **Multi-page driver design + its remaining backlog:** [docs/design/multi-page-driver.md](docs/design/multi-page-driver.md).
- **Deep per-task history (pre-2026-06-18):** [docs/progress-archive.md](docs/progress-archive.md).
