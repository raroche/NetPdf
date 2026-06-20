# NetPdf — Progress Status

> **Current state (2026-06-19):** Phase 3's layout + pagination engine drives multi-page rendering for **tables, flex, grid, multicol, and empty/explicit-height blocks**. **⚠️ Known gap (the biggest one): plain PROSE block-flow does NOT paginate** — a text-bearing `<p>`/`<div>` taller than a page overflows page 1 instead of breaking (deferrals.md `inline-only-block-pagination`). A block-granularity first cut DID paginate prose (200 `<p>` → 9 pages, no content loss) but REGRESSED two flex/grid pagination tests via an unexplained interaction, so it was reverted — the correct fix is the deferred "mid-subtree split" work. So "multi-page is live" is true for structured content but NOT yet for prose. What's left to *finish* Phase 3: (a) **prose pagination** (top priority), (b) W3C conformance **measurement** (PR 1, still awaits the A/B decision), (c) feature/polish backlog, (d) the `0.7.0-beta` release. **Perf + memory exit criteria 7–8 are now layout-pipeline smoke-gated** (PR 5 — synthetic-font p50 thresholds + a retained-heap check; the full image+web-font workload + allocation-scaling stay the BenchmarkDotNet flow).
>
> Last merged: PR [#198](https://github.com/raroche/NetPdf/pull/198) (tasks 9–11). This branch `phase3-tasks-12-13-14`: task 12 confirmed done (margin-box overflow + relative-unit resolution already implemented + tested; container units are a tracked post-v1 deferral) + tasks 13–15 (perf + memory gates). `git log --oneline -1` shows the exact commit.
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

**Gates (all green, 2026-06-19):** 7125 unit / 4 skip (+ the 3 perf/memory gates) · 30 LayoutSnapshots · 97 RealDocuments · W3cConformance (smoke only) · PaginationGolden · RenderingCorpus · 0-warning Release · AOT/JIT parity · determinism.

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

**Bottom line:** most engine work is done, but **two real multi-page gaps surfaced while gating perf (2026-06-19): (1) prose block-flow doesn't paginate** (`inline-only-block-pagination` — the top open item) and (2) allocation churn is super-linear on long docs (`multi-page-allocation-churn`). The critical path is **prose pagination → conformance measurement → release.**

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

### ▶ PR 6 — PROSE PAGINATION  [the top open gap — DO NEXT]
16. **Inline-only block pagination** (`inline-only-block-pagination`) — make a text-bearing block taller than a page break across pages. A block-granularity first cut works for prose but regressed flex/grid (reverted); the fix needs the right context guard (exclude flex/grid item-content measure recursions) + likely line-level splitting (orphans/widows). The single most impactful remaining feature — prose is the common document.

### PR 7 — Pagination / table / grid hardening  [feature]
17. Table intra-cell row splitting (cell content > remaining page height).
18. Grid shared track-sizing across continuation pages + emitted-rows extent.
19. Float-continuation propagation + recursive-block consumed-extent accounting. Plus `multi-page-allocation-churn` (per-page O(1) layout cursor — large-doc perf).

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
