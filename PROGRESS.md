# NetPdf — Progress Status

> **Current state (2026-06-18):** Phase 3's layout + pagination **engine is feature-complete and multi-page rendering is live**. What's left to *finish* Phase 3 is (a) wiring W3C conformance **measurement**, (b) a curated **feature/polish backlog**, (c) **confirming** the perf/memory gates, and (d) the **`0.7.0-beta` release**. The recommended next step is **PR 1 — Conformance measurement** (see the roadmap).
>
> Active branch: `phase3-rtl-flex-vertalign-lineheight` — tasks 6–8 (RTL flex main-axis flip · line-edge `vertical-align` line growth · `line-height` length/number/% cascade wiring). PR 2 (direction/bidi, tasks 4–6) is now COMPLETE. PR 1 (conformance) still awaits Roland's approach decision; PR-3 task 9 (inline-block per-run baseline + justify-all on `<br>`) is the next remaining item. `git log --oneline -1` shows the exact commit.
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

**Gates (all green, 2026-06-19):** 7103 unit / 4 skip · 30 LayoutSnapshots · 97 RealDocuments · W3cConformance (smoke only) · PaginationGolden · RenderingCorpus · 0-warning Release · AOT/JIT parity · determinism.

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
| 7 | Perf: 3-pg ≤ 200 ms, 20-pg ≤ 1.5 s p50 | 🟡 confirm (the 20-page multi-page case) |
| 8 | Memory linear with page count | 🟡 not separately gated |
| 9 | AOT smoke passes | ✅ |
| 10 | Determinism | ✅ |
| 11 | CHANGELOG + `0.7.0-beta` tagged | ❌ |

**Bottom line:** the hard engine work is done. The critical path is **measure → fix what measurement reveals → confirm perf → release.**

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
9. inline-block per-run baseline metrics + justify-all on internal `<br>` lines — **remaining** (the next PR-3 task).

### PR 4 — Paged-media completion  [feature]
10. Running-element real nested **block** layout (sub-boxes, not flattened text).
11. `string(name, start)` / `first-except` + compound `@page` selectors (e.g. `chapter:first`).
12. Page-margin box overflow + container-relative units in margin-box / running content.

### PR 5 — Perf + memory gates  [criteria 7–8]
13. 3-page invoice ≤ 200 ms p50 benchmark gate (confirm it runs in CI).
14. 20-page report ≤ 1.5 s p50 benchmark gate (the now-live multi-page path).
15. Memory-linearity test (linear growth with page count).

### PR 6 — Pagination / table / grid hardening  [feature]
16. Table intra-cell row splitting (cell content > remaining page height).
17. Grid shared track-sizing across continuation pages + emitted-rows extent.
18. Float-continuation propagation + recursive-block consumed-extent accounting.

### PR 7 — Release  [criterion 11]
19. **Deferral audit** — reconcile `deferrals.md` / `compatibility-matrix.md` with live state; close stale entries (especially the grid residuals, several of which already shipped).
20. CHANGELOG `0.7.0-beta` entry + exit-criteria sign-off.
21. Tag `0.7.0-beta`.

> **Backlog pool** (interleave into the PRs above as conformance findings dictate): flex `align-content: baseline`, flex `%`/`em`/`calc()` item sizing, multicol font-relative `column-width` + balancing cache, grid box-sizing/maximize/perf residuals (**audit first — many may be stale**), `outline` non-solid styles + diagnostic, `page`/`object-position` `@supports` registration, empty-resume-page sentinel cleanup, per-page abspos container geometry. Full inventory: the 2026-06-18 backlog sweep (34 items) is summarized in the conversation that produced this roadmap; ground each task against `deferrals.md` before starting.

## Where to look

- **Next action / live state:** the roadmap above; `git log --oneline -1`; or run `/phase-status`.
- **Deliberately deferred + pickup triggers:** [docs/deferrals.md](docs/deferrals.md).
- **In/out of scope:** [docs/compatibility-matrix.md](docs/compatibility-matrix.md).
- **Phase 3 plan + exit criteria:** [docs/phases/phase-3-layout-and-pagination.md](docs/phases/phase-3-layout-and-pagination.md).
- **Multi-page driver design + its remaining backlog:** [docs/design/multi-page-driver.md](docs/design/multi-page-driver.md).
- **Deep per-task history (pre-2026-06-18):** [docs/progress-archive.md](docs/progress-archive.md).
