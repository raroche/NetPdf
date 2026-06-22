# NetPdf — Progress Status

> **Current state (2026-06-21):** Phase 3's layout + pagination engine drives multi-page rendering for tables, flex, grid, multicol, prose, empty/explicit-height blocks. Conformance is MEASURED (curated suite, PR 1 [#202](https://github.com/raroche/NetPdf/pull/202)). **ALL FOUR conformance exit criteria are MET** — CSS 2.2 96.7% (29/30), Fragmentation **100% (12/12)**, Flexbox 100% (19/19), Grid 100% (15/15) (raised further by the post-`0.7.0-beta` sizing-residuals + CSS Fragmentation control PRs). The CSS 2.2 box-model gaps closed first ([#203](https://github.com/raroche/NetPdf/pull/203): box-sizing block-axis + emit/measure consistency + floats; min/max clamping explicit + auto/fill). Then the **flex/grid gap PR** closed: `gap`/`column-gap`/`row-gap` gutters (flex + grid) + flex/grid containers honoring their own explicit `width` — taking Flexbox 83.3% → 100% (criterion 4 MET) + Grid 80% → 93.3%. Residuals DEFERRED (none block a criterion): auto-height emit (`auto-height-emit-vs-pagination`), `inline-only-block-line-splitting`, `multi-page-allocation-churn`. **PR 8 — the `0.7.0-beta` release — is MERGED** ([#205](https://github.com/raroche/NetPdf/pull/205), squash `af1c210`): deferral audit + CHANGELOG `0.7.0-beta` + exit-criteria sign-off + the version bump (Directory.Build.props + version.json → 0.7.0-beta, guarded by `ReleaseVersionParityTests`); the **`0.7.0-beta` git tag is APPLIED** (annotated, → `af1c210`, pushed to origin 2026-06-21 — internal milestone tag on the private repo; NuGet ships at v1.0). The **post-`0.7.0-beta` sizing-residuals PR is MERGED** ([#206](https://github.com/raroche/NetPdf/pull/206), squash `825449b`): closed three sizing deferrals — grid `fr` tracks subtract gutters (Grid → 100%), percentage min/max-width/height (CSS 2.2 → 96.7%), percentage `column-gap`/`row-gap` (new flex case) — plus a review round (Roland [P1]/[P2] + Copilot) hardening the gutter accounting end-to-end: grid auto-fill/auto-fit count is gutter-aware, §11.6 Maximize Tracks subtracts gutters for non-fr tracks, flex `%` gaps + `%` item min/max resolve in the pre-measure (not just emission), and multicol `%` column-gap resolves against the content inline size. Perf/memory exit criteria 7–8 are signed off **measured-with-documented-residuals** (layout-pipeline smoke-gated, PR 5).
>
> Last merged: PR [#206](https://github.com/raroche/NetPdf/pull/206) (post-beta sizing residuals + review round — Grid + Flexbox 100%, CSS 2.2 96.7%, squash `825449b`). The **`0.7.0-beta` git tag is APPLIED** (→ `af1c210`, pushed 2026-06-21). **CSS Fragmentation control is DONE on branch `phase3-fragmentation-control`** (PR pending): `break-before`/`break-after` (+ legacy `page-break-*` aliases) now propagate forced-break metadata to the resolver — a forced break splits even a *fitting* ancestor in the recursive emit (`EmitBlockSubtreeRecursive`), taking **Fragmentation 9/10 → 12/12 (100%)**; `break-before:avoid` / `break-after:avoid` / `break-inside:avoid` set the boundary AvoidBreak flag (optimizer-honored); `orphans`/`widows` registered + CSS-drive `BreakResolver.OrphansRequired`/`WidowsRequired`. Residuals (documented): left/right parity blank-page insertion, avoid-is-optimizer-only (greedy production driver is cost-insensitive), per-paragraph orphans/widows (awaits `inline-only-block-line-splitting`). `git log --oneline -1` shows the exact commit.
>
> This file was consolidated from a 1.1 MB chronological log on 2026-06-18; the full per-subtask history is archived in [docs/progress-archive.md](docs/progress-archive.md). **Keep this file compact** — roll the roadmap as each PR lands; don't grow a blow-by-blow log here.

## Status at a glance

| Phase | Scope | State |
|---|---|---|
| 0 | Legal & architecture lock | ✅ Complete |
| 1 | PDF writer + text foundation | ✅ Complete |
| 2 | CSS engine + DOM pipeline | ✅ Complete |
| 3 | Fragmentainer-aware layout + pagination | 🚧 **Engine done — conformance MET (CSS 2.2 96.7% / Frag + Flex + Grid 100%); `0.7.0-beta` MERGED (#205) + TAGGED (`af1c210`)** |
| 4 | Visual parity (gradients, shadows, filters, SVG) | ⏸️ Not started |
| 5 | Packaging + release | 🔵 Interleaved — layout→PDF wiring done |

**Gates (all green, 2026-06-21):** 7186 unit / 3 skip (+ the 3 perf/memory gates) · 30 LayoutSnapshots · 97 RealDocuments · **W3cConformance (4 per-case-baseline gates; published rates CSS 2.2 97% / Frag 100% / Flex 100% / Grid 100%)** · PaginationGolden · RenderingCorpus · 0-warning Release · AOT/JIT parity · determinism.

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
| 3 | W3C CSS 2.2 layout pass-rate ≥ 90% | ✅ **MEASURED 96.7%** (29/30) — MET (box-sizing + min/max incl. percentage, emit/measure consistent); 1 residual gap: auto-height emit (`auto-height-emit-vs-pagination`) |
| 4 | W3C Flexbox pass-rate ≥ 85% | ✅ **MEASURED 100%** (19/19) — MET (gap gutters incl. percentage + container honors own `width`) |
| 5 | W3C Grid L1 pass-rate ≥ 70% | ✅ **MEASURED 100%** (15/15) — MET (gap gutters + `fr` tracks subtract the gutters from their free space) |
| 6 | W3C Fragmentation pass-rate ≥ 80% | ✅ **MEASURED 100%** (12/12) — MET (`break-before`/`break-after` + legacy `page-break-*` forced breaks propagate through fitting ancestors) |
| 7 | Perf: 3-pg ≤ 200 ms, 20-pg ≤ 1.5 s p50 | 🟡 **signed off measured-with-documented-residuals** — layout-pipeline smoke-gated (`PerformanceGateTests`: 3-pg ~42 ms, 22-pg ~400 ms — synthetic fonts + table content). The FULL-pipeline target (tables + **images + web fonts**, docs/design/performance.md) is the BenchmarkDotNet flow, not yet a build gate. |
| 8 | Memory linear with page count | 🟡 **signed off measured-with-documented-residuals** — RETAINED heap flat (gated); ALLOCATION linearity NOT met: multi-page churn is super-linear (`multi-page-allocation-churn` — the `[MemoryDiagnoser]` standard would flag it). |
| 9 | AOT smoke passes | ✅ |
| 10 | Determinism | ✅ |
| 11 | CHANGELOG + `0.7.0-beta` tagged | ✅ **DONE** — CHANGELOG `0.7.0-beta` entry written + exit-criteria sign-off (PR 8); the annotated `0.7.0-beta` git tag is APPLIED (→ `af1c210`, pushed 2026-06-21). |

**Bottom line:** **ALL FOUR conformance exit criteria are MET** (CSS 2.2 96.7% / Fragmentation 100% / Flexbox 100% / Grid 100%). PR 8 (`0.7.0-beta` release) is MERGED ([#205](https://github.com/raroche/NetPdf/pull/205), `af1c210`) and **TAGGED** (`0.7.0-beta` → `af1c210`, pushed 2026-06-21). The post-`0.7.0-beta` **sizing-residuals PR** then closed three sizing deferrals: grid `fr`+gap (Grid → 100%), percentage min/max (CSS 2.2 → 96.7%), percentage gaps (new flex case). Residuals (none block a criterion): `auto-height-emit-vs-pagination`, `inline-only-block-line-splitting`, `multi-page-allocation-churn`. The conformance story is clean (all criteria met, residuals documented).

## Phase 3 — remaining-work roadmap

Worked as **3-task PRs** (complete 3 → review → merge → next 3), in order. PRs 1–7 + the CSS 2.2 box-model PR + the flex/grid gap PR are DONE. **All four conformance exit criteria are MET.** PR 8 (the **`0.7.0-beta` release**) is MERGED ([#205](https://github.com/raroche/NetPdf/pull/205)) and TAGGED (`0.7.0-beta` → `af1c210`). A post-beta **sizing-residuals PR** ([#206](https://github.com/raroche/NetPdf/pull/206)) then closed three sizing deferrals (grid `fr`+gap, percentage min/max, percentage gaps), taking Grid + Flexbox to 100% and CSS 2.2 to 96.7%.

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

### PR 8 — Release  [criterion 11] ✅ MERGED ([#205](https://github.com/raroche/NetPdf/pull/205))
20. ✅ **Deferral audit** — reconciled `deferrals.md` / `compatibility-matrix.md` with live state: #203/#204 had already struck flex/grid container-width + documented box-sizing (BLOCK/GRID/TABLE/FLEX) + LengthPx min/max as shipped; refreshed the stale `flex-layouter-features` Status (Flexbox is 100%, gap shipped) + the matrix footer.
21. ✅ **CHANGELOG `0.7.0-beta` + exit-criteria sign-off** — added the `[0.7.0-beta]` CHANGELOG entry (staged-for-tagging pattern); signed off the phase-3 exit criteria + PROGRESS criterion 11: conformance 3–6 MET, perf/memory 7–8 measured-with-documented-residuals (smoke-gated). The review round also bumped the actual version (Directory.Build.props + version.json → 0.7.0-beta, guarded by `ReleaseVersionParityTests`) + rewrote the README to the facade-wired state.
22. ✅ **Tag `0.7.0-beta`** — APPLIED (annotated tag → `af1c210`, #205's merge commit, pushed to origin 2026-06-21) so the tag captures the signed-off state before post-beta work. Repo is PRIVATE → an INTERNAL milestone tag, NOT a public release, NO NuGet (that's v1.0).

### Post-`0.7.0-beta` — sizing residuals  ✅ MERGED ([#206](https://github.com/raroche/NetPdf/pull/206), squash `825449b`)
Three left-behind sizing deferrals, each raising a conformance category (all gates + AOT green; LTR/in-flow byte-identical):
1. ✅ **grid `fr`+gap** (`grid-gap-fr-track-sizing`) — `ResolveFrTracks` subtracts the `(n-1)*gap` gutter total from the fr leftover (percentage tracks still resolve against the full extent). Flips `grid-fr-columns-with-gap` → passing. **Grid 14/15 → 15/15 (100%).**
2. ✅ **percentage min/max** (`min-max-percentage-sizing`) — `ClampBorderBoxToMinMax` takes a containing size; a `%` min/max resolves against it (indefinite → none/0 per §10.7), threaded through all 9 in-flow clamp sites using each site's own `%`-width/height base. Flips `css22-min-width-percentage` → passing. **CSS 2.2 28/30 → 29/30 (96.7%).**
3. ✅ **percentage gaps** (`gap-percentage-sizing`) — `column-gap`/`row-gap` → `LengthPercentage`; `ReadFlexGridGapOrZero` resolves a `%` against the matching content extent (column-gap → inline, row-gap → block), threaded at the flex + grid emission sites. Adds `flex-percentage-column-gap` (Flexbox → 19/19, still 100%).
4. ✅ **review round** (Roland [P1]/[P2] + Copilot) — hardened the gutter/percentage accounting end-to-end so pre-measure and emission can't desync: grid auto-fill/auto-fit count is gutter-aware (§7.2.3.1); §11.6 Maximize Tracks subtracts gutters for non-fr growable tracks; flex `%` gaps resolve in the BlockLayouter pre-measure (a `%` row-gap on an auto-height column resolves to 0 per the indefinite-reference rule); flex item `%` min/max main-size resolves against the container main size; multicol `%` column-gap resolves against the content inline size (was silently `normal`). +13 unit tests.

### CSS Fragmentation control  ✅ DONE (branch `phase3-fragmentation-control`, PR pending) [Fragmentation criterion 6 → 100%]
The `BreakResolver` engine already had forced-break (`ForceBreak` → `BreakHere`) + orphans/widows + `break-inside: avoid` cost; this PR wired the CSS:
1. ✅ **`break-before` / `break-after`** (+ legacy `page-break-before`/`-after` aliases) — registered in `properties.json` + KeywordResolver; `ForcesPageBreakBefore`/`After` readers (modern wins over legacy). Forced breaks propagate to BOTH break-decision sites: the top-level loop AND `EmitBlockSubtreeRecursive`'s nested-block decision (so a `break-before:page` on a grandchild splits its *fitting* `body`/`html` ancestors — the named-page forced-break precedent at ~L4951). **Frag 9/10 → 12/12 (100%)** (flips `frag-break-before-page` + adds `frag-break-after-page` + `frag-page-break-before-always`).
2. ✅ **`break-inside: avoid`** (+ `break-before:avoid` / `break-after:avoid`) — `AvoidsBreakInside` / `AvoidsPageBreak*` readers set the boundary `AvoidBreak` flag (every internal boundary of a `break-inside:avoid` box). Honored by the optimizing resolver's cost; the production greedy resolver is cost-insensitive (residual — block-flow children are already atomic so it's not visibly wrong today).
3. ✅ **`orphans` / `widows`** — registered (`Integer`, inherited, default 2) + `ReadOrphansOrDefault`/`ReadWidowsOrDefault`; `PdfRenderPipeline` reads the **body box** (NOT the synthetic root, which carries the initial default — PR #207 review [P2]) into `new BreakResolver(orphans, widows)`. Visible effect awaits line-level splitting (`inline-only-block-line-splitting`); per-paragraph overrides need a per-paragraph resolver value (residual).

Residuals (documented in `deferrals.md#fragmentation-control-residuals`): left/right/recto/verso parity blank-page insertion (left/right behave like `page` today; `recto`/`verso`/`all` aren't parsed by AngleSharp.Css 1.0.0-beta.144 yet); avoid-is-optimizer-only; per-paragraph orphans/widows.

### Next 3-task PR — pick at the fork (left-behind deferrals first)
Candidates (ground against `deferrals.md` before starting): (a) **Flex L1 completion** — `flex` shorthand parser (`flex: 1` silently dropped today — very common), `flex-basis` intrinsic keywords (`content`/`min-content`/`max-content`), `align-items: baseline` (all in `flex-layouter-features`; high real-world value, no conformance bump since Flexbox is 100%). (b) **`auto-height-emit-vs-pagination`** → CSS 2.2 to 100%, but DEEP (per-page-fragment extents; previously attempted + reverted). (c) **Phase 4** — visual parity (gradients, shadows, filters, SVG), the next major phase.

> **Backlog pool** (interleave into the PRs above as conformance findings dictate): flex `align-content: baseline`, flex `%`/`em`/`calc()` item sizing, multicol font-relative `column-width` + balancing cache, grid box-sizing/maximize/perf residuals (**audit first — many may be stale**), `outline` non-solid styles + diagnostic, `page`/`object-position` `@supports` registration, empty-resume-page sentinel cleanup, per-page abspos container geometry. Full inventory: the 2026-06-18 backlog sweep (34 items) is summarized in the conversation that produced this roadmap; ground each task against `deferrals.md` before starting.

## Where to look

- **Next action / live state:** the roadmap above; `git log --oneline -1`; or run `/phase-status`.
- **Deliberately deferred + pickup triggers:** [docs/deferrals.md](docs/deferrals.md).
- **In/out of scope:** [docs/compatibility-matrix.md](docs/compatibility-matrix.md).
- **Phase 3 plan + exit criteria:** [docs/phases/phase-3-layout-and-pagination.md](docs/phases/phase-3-layout-and-pagination.md).
- **Multi-page driver design + its remaining backlog:** [docs/design/multi-page-driver.md](docs/design/multi-page-driver.md).
- **Deep per-task history (pre-2026-06-18):** [docs/progress-archive.md](docs/progress-archive.md).
