# NetPdf — Progress Status

> **Current state (2026-06-21):** Phase 3's layout + pagination engine drives multi-page rendering for tables, flex, grid, multicol, prose, empty/explicit-height blocks. Conformance is MEASURED (curated suite, PR 1 [#202](https://github.com/raroche/NetPdf/pull/202)). **The CSS 2.2 box-model gaps closed:** `box-sizing: border-box` (block-axis emit + the subtree MEASURE so emit/pagination agree + floats) and `LengthPx` min/max-width/height clamping (explicit AND auto/fill), taking **CSS 2.2 84.2% → 93.3% (28/30) — exit criterion 3 MET**. Two CSS 2.2 residuals DEFERRED: auto-height shrink-to-fit (`auto-height-emit-vs-pagination` — growing the emitted height regresses multi-page pagination) + percentage min/max (`min-max-percentage-sizing` — LengthPx only). Measured rates now: CSS 2.2 93.3%, Fragmentation 90%, Flexbox 83.3%, Grid 80% — **3 of 4 MET**; only Flexbox below target (container-own-width gap). What's left to *finish* Phase 3: (a) optionally close the Flexbox container-width gap to clear criterion 4; (b) niche residuals (`inline-only-block-line-splitting`, `multi-page-allocation-churn`, `auto-height-emit-vs-pagination`, `min-max-percentage-sizing`, nested table/multicol in floats); (c) the **`0.7.0-beta` release** (PR 8). Perf/memory exit criteria 7–8 are layout-pipeline smoke-gated (PR 5).
>
> Last merged: PR [#202](https://github.com/raroche/NetPdf/pull/202) (PR 1 — W3C conformance measurement: curated suite + per-case baseline). Current branch `phase3-css22-box-model-gaps`: box-sizing (block axis), min/max clamping, auto-height (attempted → deferred). `git log --oneline -1` shows the exact commit.
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

**Gates (all green, 2026-06-21):** 7147 unit / 4 skip (+ the 3 perf/memory gates) · 30 LayoutSnapshots · 97 RealDocuments · **W3cConformance (4 per-case-baseline gates; published rates CSS 2.2 93% / Frag 90% / Flex 83% / Grid 80%)** · PaginationGolden · RenderingCorpus · 0-warning Release · AOT/JIT parity · determinism.

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
| 3 | W3C CSS 2.2 layout pass-rate ≥ 90% | ✅ **MEASURED 93.3%** (28/30) — MET (box-sizing + min/max fixed, emit/measure consistent); 2 residual gaps: auto-height emit (`auto-height-emit-vs-pagination`), percentage min/max (`min-max-percentage-sizing`) |
| 4 | W3C Flexbox pass-rate ≥ 85% | 📊 **MEASURED 83.3%** (10/12) — OPEN, below target; gaps: flex `gap`, container ignores own `width` (explicit-width case counted head-on per PR 1 review) |
| 5 | W3C Grid L1 pass-rate ≥ 70% | ✅ **MEASURED 80.0%** (8/10) — MET (gap: column-gap/row-gap) |
| 6 | W3C Fragmentation pass-rate ≥ 80% | ✅ **MEASURED 90.0%** (9/10) — MET (gap: break-before:page) |
| 7 | Perf: 3-pg ≤ 200 ms, 20-pg ≤ 1.5 s p50 | 🟡 **layout-pipeline smoke-gated** (`PerformanceGateTests`: 3-pg ~42 ms, 22-pg ~400 ms — synthetic fonts + table content). The FULL-pipeline target (tables + **images + web fonts**, docs/design/performance.md) is the BenchmarkDotNet flow, not yet a build gate. |
| 8 | Memory linear with page count | 🟡 **partial** — RETAINED heap flat (gated); ALLOCATION linearity NOT met: multi-page churn is super-linear (`multi-page-allocation-churn` — the `[MemoryDiagnoser]` standard would flag it). |
| 9 | AOT smoke passes | ✅ |
| 10 | Determinism | ✅ |
| 11 | CHANGELOG + `0.7.0-beta` tagged | ❌ |

**Bottom line:** **3 of 4 conformance exit criteria MET** (CSS 2.2 93.3% / Fragmentation 90% / Grid 80%) after the CSS 2.2 box-model fixes (box-sizing block-axis + emit/measure consistency + floats; min/max clamping on explicit + auto sizes) cleared criterion 3. Only **Flexbox 83.3% is OPEN** (below 85%; gaps: flex `gap`, container ignores own `width`). Two CSS 2.2 residuals stay deferred — auto-height emit (`auto-height-emit-vs-pagination`) + percentage min/max (`min-max-percentage-sizing`). Critical path now: (a) optionally close the Flexbox container-width gap to clear criterion 4, then (b) the **`0.7.0-beta` release** (PR 8 — deferral audit + CHANGELOG + tag).

## Phase 3 — remaining-work roadmap

Worked as **3-task PRs** (complete 3 → review → merge → next 3), in order. PRs 1–7 + the CSS 2.2 box-model PR are DONE. **Criterion 3 is now MET (93.3%)**; the recommended next PR either closes the Flexbox container-width gap (clears criterion 4) or goes straight to the **`0.7.0-beta` release** (PR 8). Surface the fork to Roland if unsure.

### CSS 2.2 box-model gaps  [clears criterion 3] ✅ DONE
1. ✅ **`box-sizing: border-box` (block axis)** — the recursive subtree emitter added padding/border OUTSIDE the declared height (inline axis already honored box-sizing); routed it through `BoxSizingHelper`. Flips `css22-box-sizing-border-box` + 3 new cases.
2. ✅ **min/max-width/height clamp a size** (§10.4/§10.7) — added `ClampBorderBoxToMinMax` (block mirror of the flex min/max reader), applied at the in-flow width/height sites (explicit + auto/fill) + the subtree measure + floats (review fixes — emit/measure consistency). Flips `css22-min-width-on-explicit` + 7 new cases. **CSS 2.2 → 93.3% (28/30), criterion 3 MET.** (percentage min/max → `min-max-percentage-sizing` deferral.)
3. ⏸️ **auto-height shrink-to-fit** — ATTEMPTED + reverted: emitting the effective (content-spanning) height regresses multi-page block-flow pagination (forced-overflow vs clean splits). Deferred as `auto-height-emit-vs-pagination`; criterion 3 already met without it.

### PR 1 — Conformance measurement  [criteria 3–6] ✅ DONE (PR [#202])
1. ✅ **Harness** — replaced the `NetPdf.W3cConformance` smoke stub with a CURATED assertion suite (Roland's A/B call: curated NetPdf cases over vendored WPT). Drives the internal pipeline (`Phase2Pipeline`→`BlockLayouter`) + asserts `BoxFragment` geometry. CSS 2.2 subset (19 cases) → **84.2%**.
2. ✅ **CSS 2.2 + Fragmentation pass-rates** — gated at regression floors met today; Fragmentation (10 cases) → **90.0%** (MET ≥80%).
3. ✅ **Flexbox + Grid pass-rates** — Flexbox (12) → **83.3%** (below ≥85%, container-width gap counted head-on per review); Grid (10) → **80.0%** (MET ≥70%); four numbers published in `tests/NetPdf.W3cConformance/README.md`. Gates assert a **per-case baseline** (every non-`KnownGap` case must pass; every `KnownGap` case must still fail) — not a pass-rate floor (PR 1 review [P1]); the published rates sit next to their roadmap targets.

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
