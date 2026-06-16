# Design — the multi-page driver

**Status:** IN PROGRESS — **Cycles 0–8 done** (nested-container fragmentation + the pipeline driver loop + per-page counters + cross-page running content [cycles 5/5b] + per-page `@page :first`/`:left`/`:right`/`:blank` margin boxes [cycle 6] + named pages [cycle 7] + corpus/golden composition verification [cycle 8]). The **font-dedup-across-pages follow-up is DONE** (a shared font is now embedded once, not once per page). **NON-BLOCK PAGINATION — audit + flex-column DONE:** the cycle-8 "non-block modes don't paginate" finding was a FALSE NEGATIVE (its probe used explicit cell/item heights that collapse short, so the test containers never exceeded one page). An empirical re-audit found TABLE row-split, `<thead>`/`<tfoot>` REPEAT, MULTICOL, and GRID-with-`grid-template-rows` ALL already paginate (now pinned with facade tests). The genuine gaps were FLEX-COLUMN (DONE — `flex-direction: column` + nowrap splits at item boundaries via `FlexContinuation.ItemIndex` + a natural-size/page-budget dual-input) and GRID-with-implicit-rows (`grid-auto-rows` — now DONE: `PreMeasureGridRowExtent` measures implicit tracks so auto-row grids overflow + paginate). **FLEX item CONTENT layout, GRID content-sized auto rows, and the explicit-height flex-column spurious forced-overflow warning are now DONE** (flex items render their text + column auto-height items content-size; grid `auto` rows size + paginate from cell content; the spurious warning is gone via a paginatable-flex subtree-extent projection). Remaining backlog (deferrals.md, each substantial/risky): content-SIZED auto-height column-flex PAGINATION (needs a content-aware flex pre-measure), row-flex main-axis split + column-reverse/wrap, compound `@page`, `page`/`object-position` registration. The §8 decisions were taken per the recommended defaults (#3 greedy resolver, #4 child-boundary granularity) — see the Progress log.
**Owner:** Phase 3 (layout + pagination), interleaved with Phase 5 layout→PDF wiring.
**Related:** [phase-3-layout-and-pagination.md](../phases/phase-3-layout-and-pagination.md) · [deferrals.md#layout-to-pdf-pipeline](../deferrals.md) · [determinism.md](determinism.md) · [performance.md](performance.md)

---

## TL;DR

NetPdf renders a single page today. The pagination *engine* (`NetPdf.Paginate`) is **already built and partially wired** — break resolver, cost model, bounded-DP optimizer, continuation tokens, checkpoint/rewind, and a retry coordinator all exist and are unit-tested. A **working driver-loop prototype** is preserved on the local branch `wip-multi-page-driver-blocked`.

The thing that actually blocks multi-page output is **one layout-engine gap**: `BlockLayouter` fragments only the layout **root's direct children** across pages. A *nested* block container lays out **all** its children on the current page. Because every facade document nests content under `html → body`, real content never paginates.

So this is **not** "build a pagination system." It is, in order:

1. **Nested-container fragmentation in `BlockLayouter`** — the one hard layout task. (Largest, riskiest.)
2. **Wire the driver loop** — restore + modernize the prototype; integrate per-page margin boxes + real page counters. (Mostly mechanical.)
3. **Paged-media per-page context** — `@page :left/:right/:blank`/named pages + the `page` property; cross-page running-content persistence. (Cascade-shaped, independent of 1–2.)

This doc proposes the design for each, a phased PR breakdown that fits the project's one-task-per-PR review cadence, and a set of **open scope decisions** that need your call before I start.

---

## Progress log

- **2026-06-16 — PR #183 review cycle applied (3 findings)** (same branch). **(P2)** Valid DASHED page
  names (`--chapter`) are now accepted: `PageNameResolver.IsCustomIdent` and `AtPageRules.IsBarePageName`
  were duplicated and BOTH wrongly rejected `--name`, even though a dashed ident is a valid
  `<custom-ident>` (CSS Syntax 3 §4.3.9 accepts a leading `-` followed by another `-`). Centralized into
  the new shared `CssCustomIdent.IsValidPageName` (`NetPdf.Css.Parser`), used by both; `page: --chapter`,
  `@page --chapter`, `@page --chapter:first`, `@supports (page: --chapter)`, `DeclaredPageNames`, and
  `ResolveUsedPageName` all work (resolver/MatchTier/DeclaredPageNames/ResolveUsedPageName/facade tests).
  **(P3)** `<position>` parsing is PAREN-AWARE: `PositionResolver` and
  `FragmentPainter.TryParseBackgroundPosition` now tokenize via `CssShorthandHelpers.SplitTopLevel`, so a
  math function (`object-position: calc(50% - 10px) top`) stays one component instead of fragmenting into
  broken tokens — `@supports` validates the expression, and the painter evaluates a %/absolute math
  function against the §3.6 range (`TryEvalPositionMath`); a font-/viewport-relative math function has no
  context in the static painter helper and falls back to the 0% 0% default + diagnostic (documented
  limitation). **(P3)** Stronger column-reverse continuation tests (`DriveFlexColumnPages`): VARIED item
  heights over THREE pages + an `order` variant, asserting VISUAL reverse-DOM order, top re-anchoring, no
  loss/duplication, and clean termination (proving the per-page `FlexContinuation.ItemIndex` re-anchors
  correctly). Gates: **6901 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · W3cConformance ·
  0-warning Release**.

- **2026-06-15 — BACKLOG #4–#7 (5 tasks) DONE** (branch `phase-3-backlog-flex-pagination-paged-media`). The
  REMAINING prioritized backlog, one PR. **(#7) Content-aware flex pre-measure:** `PreMeasureFlexMainExtent`
  now measures a content-determined (auto-height) item's content block extent (memoized; mirrors grid's
  `PreMeasureGridRowExtent`), so an AUTO-height column-flex whose items are content-sized overflows the wrapper
  + paginates (PR #182 left this deferred — the pre-measure summed declared heights only). **(#4) Column-reverse
  pagination:** a tall `flex-direction: column-reverse` paginates at item boundaries in VISUAL (reverse-DOM)
  order — the emission reverses the (per-attempt) item sequence + emits forward, reusing the forward column
  item-split; gated to the paginating case so non-paginating column-reverse is byte-identical. **(#5) Compound
  `@page` selectors:** `AtPageRules.MatchSelector` matches `<name>:<pseudo>` (e.g. `chapter:first`) at tiers
  4/5 above the bare named page (3) per CSS Page 3 §3.1 — existing single-selector tiers unchanged (no churn);
  reachable via the per-page margin-box path. **(#6) Register `page` + `object-position`:** both gained a
  properties.json entry + a validating resolver (`PageNameResolver` / `PositionResolver`), so `@supports`
  answers correctly + invalid values diagnose — SAFE because the cascade winner's `ResolvedValue` is the
  var-substituted RAW value (the painter / named-page machinery still read raw). DEFERRED: column-wrap +
  row-flex intra-item fragmentation; pure/multi-pseudo compound `@page`; `<position>` axis-conflict rules.
  Gates: **6885 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning Release · AOT/JIT parity
  verified**.

- **2026-06-15 — FLEX/GRID item CONTENT + spurious-overflow (prioritized backlog #1+#2+#3) DONE** (branch
  `phase-3-flex-grid-item-content`). The top three non-block-pagination backlog items, one PR. **(#1) Flex item
  CONTENT layout:** `FlexLayouter` lays out each item's inner content (text / block children) via a nested
  `BlockLayouter` into a per-item `BufferingMeasureSink`, flushed at the item's FINAL (re-anchored) content-box
  origin (only COMMITTED items flush; out-of-flow descendants stay outer-pass-owned — the
  `PostPr114_abspos_in_flex_item_content` contract holds). Column AUTO-height items CONTENT-SIZE to the measured
  block extent (closing the `<div>text</div>` collapse-to-0 gap). **(#2) Grid CONTENT-sized rows:**
  `GridSizing.Resolve` gained an optional `GridContentMeasurer`; `ItemOuterContribution` measures a cell's
  content block extent at its column width when the height is auto/0 (was 0 → the zero-cell skip). Wired at the
  `GridLayouter` emission Resolve AND `PreMeasureGridRowExtent` (so content-sized auto-row grids PAGINATE),
  memoized per item box. **(#3) Spurious `PAGINATION-FORCED-OVERFLOW-001`:**
  `MeasureSubtreeVisualBlockExtentRecursive` PROJECTS a paginatable flex descendant to `min(authored, pageSize)`
  — exactly like the existing paginatable-grid projection — so an auto-height wrapper no longer trips the
  ancestor forced-overflow path on an explicit-height column flex's rigid height. **Enablers:** a new OPT-IN
  `BlockLayouter.layoutRootInlineContent` flag (a nested layouter whose root is itself inline-only emits its own
  inline content — the common `<div>text</div>` item, whose direct inline child the block-only child loop
  skipped); shared `NestedContentMeasurer` + `BufferingMeasureSink`; and `BoxFragment.SuppressBoxDecoration` +
  a `FragmentPainter` guard so a content fragment (box == the item) paints text-only and doesn't double-paint
  the item's background/border (a translucent fill made the doubling visible). Pre-commit review (subagent):
  no P1; P2 grid measurement memoized; P3 doc/test nits applied. DEFERRED (deferrals.md prioritized backlog,
  now #4–#7): content-SIZED auto-height column-flex PAGINATION (needs a content-aware `PreMeasureFlexMainExtent`,
  #7 — grid's pre-measure already content-measures); row main-axis content-WIDTH sizing; grid content-WIDTH
  columns; column-reverse/wrap + row-flex; compound `@page`; `page`/`object-position` registration. Gates:
  **6869 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning Release · AOT/JIT parity verified**.

- **2026-06-15 — GRID implicit-rows pagination DONE** (branch `phase-3-nonblock-pagination-completion`). A grid sized by IMPLICIT tracks (`grid-auto-rows` / auto-placed rows, NOT `grid-template-rows`) now paginates — completing grid pagination for the common case (grid pagination already worked with explicit `grid-template-rows`, pinned in PR #180). Root cause + fix: `BlockLayouter.PreMeasureGridRowExtent` early-returned 0 on an empty `grid-template-rows`, so an auto-row grid stayed chrome-height + never overflowed + never triggered the (already-wired) grid pagination. Removed the early-return — `GridSizing.Resolve` already generates implicit tracks (CSS Grid §7.4), so the natural row extent is now measured for explicit AND implicit rows (also fixes a latent overlap: an auto-row grid's following siblings no longer overlap its rows). Covered at the recursion path (`Production_implicit_auto_row_grid_paginates_when_taller_than_page` unit + `Grid_with_implicit_auto_rows_paginates_across_pages_without_loss` facade), with `1fr` columns (`Grid_with_fr_column_and_implicit_rows_paginates`), and as the root's direct child / outer dispatch (`Grid_as_root_child_with_implicit_rows_paginates_via_outer_dispatch`). DEFERRED (prioritized backlog in deferrals.md — each a substantial/risky standalone task): flex item CONTENT layout (items render no text); grid content-sized auto rows (`grid-auto-rows: auto` cells collapse to 0); explicit-height flex-column spurious `PAGINATION-FORCED-OVERFLOW-001` (correct output, delicate break-logic fix); column-reverse/wrap + row-flex; compound `@page` selectors; `page`/`object-position` registration. Gates: **6854 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning Release · AOT/JIT parity verified**.
  - **Post-PR-#181 review (3 findings, all addressed; same PR).** **(P1, real bug)** `PreMeasureGridRowExtent` passed a FAKE `contentInlineSize: 1` — for an auto-repeat column template (`repeat(auto-fill|auto-fit, …)`) `GridSizing.ComputeAutoRepeatIterations` derives the column count from the inline extent, so a 602px grid would resolve 1 column at pre-measure (vs 6 at dispatch) → inflated row count → FALSE pagination / wrapper growth. Fixed by threading the grid's REAL content inline size (derived via `GridGeometryHelper.ComputeContentGeometry`, matching the dispatch) at both the outer + recursive pre-measure call sites; the block axis stays the indefinite signal (these are `IsHeightAuto` grids whose grown extent is what the method computes). Test: `Grid_auto_fill_columns_does_not_falsely_paginate` (6 items in `repeat(auto-fill,100px)` × 200px stay ONE page — would be 2 with the fake width). **(P2)** the pre-measure ran `GridSizing.Resolve` with `cancellationToken: default` (uncancellable dry run); the existing token is now threaded through `PreMeasureGridRowExtent` → `Resolve`. Test: `Grid_with_many_implicit_rows_paginates_at_scale_without_loss` (60-row scale sanity). **(P3)** refreshed the now-stale `PreMeasureGridRowExtent` XML doc + comments (they still claimed "sums explicit length rows, returns 0 without grid-template-rows"). Gates: **6856 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning Release · AOT/JIT parity verified**.

- **2026-06-14 — Cycle 0 (blocker confirmed) DONE.** Verified the nested-container fragmentation blocker still reproduces on `main` @ `c09198a` (the deferral's probe was measured on the long-superseded `b63351e`). Added a green characterization test, [`MultiPageDriver_blocker_nested_wrapper_lays_out_all_children_on_one_page`](../../tests/NetPdf.UnitTests/Phase3/BlockLayouterTests.cs), driving `root > wrapper(auto) > [6 × 200px]` on a 500px page directly through `BlockLayouter.AttemptLayout`. Confirmed exactly: page 0 returns `PageComplete` + a `BlockContinuation`, yet emits **all 7 fragments** (wrapper + 6 children, last bottom = 1200px — overflowing the 500px page); the resume page is `AllDone` with **0 fragments**. So nested content genuinely does not paginate.
  - **Key finding:** the *desired* post-fix behavior is **already pinned** by an existing skipped test, [`Cycle2d_oversized_subtree_splits_across_two_pages_at_inner_break`](../../tests/NetPdf.UnitTests/Phase3/BlockLayouterTests.cs) (`BlockLayouterTests.cs:4641`). Its skip reason already names the fix mechanism: a recursive continuation token (`BlockContinuation.NestedContinuation`) + break consultation **inside** `EmitBlockSubtreeRecursive` + recursive resume on retry. Cycle 1 implements that, un-skips `Cycle2d`, and flips the new characterization test to assert a clean child-boundary split.
  - **Next:** cycle 1 (§5) — pending the §8 scope sign-off (esp. decisions #3 break-resolver and #4 frag-depth, which shape cycle 1's "done").

- **2026-06-14 — Cycles 1 + 2 (nested-container fragmentation) DONE** (branch `phase-3-multi-page-cycle-1-nested-fragmentation`). `EmitBlockSubtreeRecursive` now consults the propagating resolver before each plain block-flow child and returns a `BlockContinuation` on block-axis overflow. The fix **reuses the existing `BlockContinuation.LayouterState` chain slot + the caller-side propagation** (both the normal walk and the forced-overflow path already wrapped + propagated a returned `BlockContinuation`), so the proposed dedicated `NestedContinuation` field was unnecessary — and it works at **arbitrary depth for free** (the recursion's self-call threads the resolver down; the planned cycle-2 work was already covered). §8 decisions taken per the recommended defaults: **greedy resolver (#3)**, **child-boundary granularity (#4)** — a single child taller than the page still force-overflows (no intra-child line split). Gating: forward-progress via `childCursor > 0` (oversized first child force-emits, never spins); **block-flow children only** (`IsBlockFlowContainerOwnedByBlockLayouter`) so flex/grid/table/multicol still paginate internally; floats excluded (their recursion sites omit the resolver); `position: fixed` excluded via `SuppressBlockPagination`. Tests: rewrote the cycle-0 probe → `MultiPageDriver_cycle1_nested_wrapper_children_split_across_pages_at_child_boundaries` (2/2/2 split), un-skipped `Cycle2d_oversized_subtree_splits_across_two_pages_at_inner_break`, added a depth-2 `..._works_at_arbitrary_depth`. One flex-pagination regression caught + fixed (the block-flow-only gate). Gates: **6761 unit / 5 skip · 30 snapshots · 97 realdocs · 0-warning Release · AOT/JIT parity verified**.
  - **Known artifacts → cycle 3.** (a) The forced-overflow continuation protocol signals one trailing EMPTY resume page (the resume-page subtree measure isn't resume-aware, so a wrapper re-measures as oversized once its remaining children fit); the driver skips painting it; resume-aware measure is a later refinement. (b) The wrapper fragment repeats full-size on each page it spans (per-page "partial" sizing deferred).
  - **Next:** cycle 3 — the pipeline-level multi-page driver loop (restore the prototype; **layout-all-then-paint** per §4.5; skip empty resume pages; real per-page counters + margin boxes).

- **2026-06-14 — Post-PR-#175 review (4 items, all addressed).** **(P1, real bug)** the nested forward-progress guard was the *signed* `childCursor > 0`, which mis-signals progress — a prior nested FLOAT emits a fragment without advancing `childCursor`, and a NEGATIVE-margin prior child can leave it ≤ 0 after real emission, both wrongly suppressing the break (collapsing nested pagination back to forced overflow). Replaced with a fragment-count baseline (`_sink.Cursor` captured at recursion entry; the break fires only once it advances) + regression tests for the float and negative-margin cases. **(P2)** the break now actually calls `propagatingResolver.ConsiderBreakAt` (it had hard-coded the overflow test): the recursion sets the fragmentainer's `UsedBlockSize` transiently to the child's block-start so the greedy resolver's `RemainingBlockSize` fit-check is correct, then restores it. **Deferred at nested boundaries** (documented in-code): checkpoint registration + `BreakAction.Rewind` (greedy never rewinds; the recursion holds no checkpoint) and break-before/-after/-inside metadata on the opportunity. **(P2-tests)** the multi-page tests now assert the EXACT page sequence + termination (final `AllDone`, null continuation, ≤ 1 trailing empty page), so a non-terminating "keeps returning empty PageComplete" regression can't slip through. **(P3)** added edge coverage: `SuppressBlockPagination` (no nested break), a first oversized child force-overflowing not looping, plus the P1 float/negative-margin tests; flex/grid/table/multicol-not-whole-box-deferred stays covered by `Task16_cycle4_closeout...`.

- **2026-06-14 — Cycles 3 + 4 (driver loop + per-page counters) DONE** (branch `phase-3-multi-page-cycles-3-5-driver`). `PdfRenderPipeline.RenderAsync` is restructured into **layout-all-then-paint** (§4.5): PHASE A loops `LayoutRetryCoordinator.Run` over the continuation, accumulating each page's fragments (skipping the one trailing empty resume page; the overflow diagnostic is narrowed to inline-overflow/cap — block content now FLOWS across pages); PHASE B paints each page with `PageCounters(pageIndex+1, totalPages)`, re-running the margin-box pass per page (cycle 4). So a tall document paginates AND `counter(page)`/`counter(pages)` are correct per page. Tests: `Content_taller_than_one_page_paginates_across_multiple_pages` (replaced the single-page overflow test), `Multi_page_footer_page_counter_increments_per_page` + `..._total_counter_is_the_real_page_count` (per-page counter values — an "A"-prefixed footer makes the value observable past the per-page font subset). Gates: **6767 unit / 5 skip · 30 snapshots · 97 realdocs · 0-warning · AOT/JIT parity verified** (single-page output byte-identical).
  - **Discovered follow-up — font dedup across pages.** Text is subset PER PAGE (each page's `TextPainter` call re-embeds its glyphs), so a font shared across pages isn't deduped. A size/efficiency issue, NOT correctness — the prototype's "fonts dedup across pages" intent needs a 2-pass text paint (collect glyphs across all pages, subset once, emit per page). Tracked for a later cycle.
  - **Next:** cycle 5 — cross-page running content. Substantial enough to be its own PR: per-page `string()`/`element()` first/last with carry-forward needs an element→page correlation (from the per-page fragment lists' `Box.SourceElement`) + a per-page `MarginContentCollector` (the collector currently produces whole-document first/last across 10 dictionaries — named strings + running elements + their styles/segments/containers).

- **2026-06-15 — Cycle 5 (cross-page running content) DONE** (its own PR, branch `phase-3-multi-page-cycle-5-cross-page-running`). `string(name)` now resolves to the value CURRENT on each page (CSS GCPM L3 carry-forward — a named string set on an earlier page persists until re-set). New `MarginContentCollector.CollectPerPage(root, cascade, elementToPage, pageCount)` records the `string-set` assignments in document order (with their setting element), buckets them by the page that element laid out on, and carries each name forward (per page: `NamedStrings`/`First` = the carried value, overridden by the page's own assignments). The driver builds the `elementToPage` map from the per-page fragment lists' `Box.SourceElement` (first page wins) and threads `marginContexts[pageIndex]` into PHASE B. Tests: `MarginContentCollectorTests.CollectPerPage_carries_a_named_string_forward_until_re_set` (page 0 "A" → page 1 carried "A" → page 2 re-set "C"); `HtmlPdfConvertTests.Multi_page_running_header_shows_the_current_section_string_per_page` (two sections, page 1 header reads its section's string, page 2 reads the next — the "A"-prefix trick again sees past the per-page font subset). DEFERRED (documented): `element()` running content stays WHOLE-DOCUMENT (its per-page styles/segments/containers carry-forward is a follow-up); `string(name, start)` / `first-except`.

- **2026-06-15 — Cycle 5b (cross-page `element()` running content) DONE** (branch `phase-3-multi-page-cycle-5b`). `MarginContentCollector.CollectPerPage` now buckets the RUNNING-occurrence record per page the same way it already did the named strings, completing the PR-#177 follow-up. The `Walk` records every `position: running()` occurrence in document order as one `RunningOccurrence` (element + name + text + own style + per-line segments + container bands — the whole payload TOGETHER, so the per-page first/last selection can't split it across occurrences: the PR-#151 lockstep, now per page). `CollectPerPage` resolves each occurrence's page via the SAME `ResolvePage` nearest-rendered-ancestor walk as the named strings (a running element is removed from flow, so its page comes from its containing block), with a **page-0 fallback** so an all-running body with no in-flow fragment still shows its header (unlike a dropped named string, a running element IS the box's content). It then carries each name's occurrence forward until re-set; per page `element(name)` / `first` reads the FIRST occurrence on the page (or carried), `element(name, last)` the LAST. New `ProjectRunningOccurrences` projects a per-page `name → RunningOccurrence` map into the four parallel context dictionaries (text / style / segments / containers). **Single-page short-circuit:** `pageCount == 1` returns the whole-document context directly (byte-identical to the pre-cross-page path — every occurrence is on page 0), which keeps all the existing single-page `element()` facade tests unchanged and sidesteps element→page resolution for them. Tests: 4 new `MarginContentCollectorTests` (per-page bucketing — the un-skipped cycle-5b pin; carry-forward across an empty page; first/last within one page; style/text lockstep per page) + a single-page-short-circuit pin + `HtmlPdfConvertTests.Multi_page_running_header_shows_the_current_section_element_per_page` (the `element()` analogue of the cycle-5 named-string facade test: "A" `element(rh)` reads "AA" on page 1 and "AB" on page 2 — the whole-doc collector gave "AA" on both). Gates: **6777 unit / 5 skip · 30 snapshots · 97 realdocs · 0-warning · AOT/JIT parity verified**. APPROXIMATION (documented): sibling running elements sharing one multi-page containing block bucket to that block's first page (nearest-ancestor can't localize further — they degrade to whole-doc first/last, no worse than before); nesting each in its own per-page container resolves them per page.
  - **Next:** cycle 6 — `@page :left` / `:right` / `:blank` (a `PageSelectorContext` + generalize `ClassifyPageSelector` → `MatchesPageContext`; parity from page index, the `:first` path as precedent).

- **2026-06-15 — Cycle 6 (`@page :first`/`:left`/`:right`/`:blank` margin boxes) DONE** (same branch `phase-3-multi-page-cycle-5b`, bundled PR). `AtPageRules` gained `PageSelectorContext(PageIndex, IsBlank)` — `IsFirstPage` + the LTR `IsRightPage` parity (page 0 = recto/right; sides alternate; RTL flip out of scope) — and `MatchTier(prelude, ctx)`, the page-context generalization of `ClassifyPageSelector`: it returns the HIGHEST matching CSS Page 3 §3.1 specificity tier (bare 0 < `:left`/`:right` 1 < `:first`/`:blank` 2) over the selector list, or −1 when nothing matches (named pages + compounds stay deferred → cycle 7). New rule-only walks `EnumeratePageRules(sheets, media, ctx)` (matching rules in ascending-tier cascade order) + `EnumerateAllPageRules` (every rule, for structural/prefetch). `AtPageMarginBoxResolver` gained context-aware `Resolve(ctx)` / `PageContextDeclarations(ctx)` (sharing extracted `ResolveFrom` / `PageContextDeclarationsFrom` cores) + `ResolveAll` (the union). The driver now resolves the page margin boxes + page-context declarations PER PAGE in PHASE B from `PageSelectorContext(pageIndex, IsBlank: bodyFragments.Count == 0)`, while the EARLY resolve (prefetch + "has margin boxes" detection) switched to `ResolveAll` so a `@page :left { … background-image }` box's image still prefetches and a `:left`-only document still runs the margin pass. So a left page paints `@page :left`'s header, the first page `@page :first`'s, etc., over the bare `@page`, with `!important` still outranking selector specificity. **SCOPE:** per-page margin-box CONTENT/STYLE only; per-page GEOMETRY (margins / page size differing by `:left`/`:right` — which would reflow LAYOUT) stays deferred (needs an iterative layout; documented). `:blank` matches a body-fragment-less page but the driver doesn't yet emit mid-document blank pages (no forced parity breaks), so it's latent — unit-tested via `MatchTier`/`Resolve(ctx)`. Tests: `AtPageRulesTests` (15 — context parity + `MatchTier` per selector/list/named/compound) + 6 new `AtPageMarginBoxResolverTests` (`Resolve(ctx)` for `:left`/`:right`/`:blank`/`:first` precedence + bare-`!important` + `ResolveAll` union) + 2 facade tests (`Multi_page_first_selector_margin_box_paints_only_on_the_first_page`, `Multi_page_left_and_right_selector_margin_boxes_alternate_by_parity`). Gates: **6800 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning · AOT/JIT parity verified**.
  - **Next:** cycle 7 — named pages (the `page` CSS property in the cascade [inherited `<custom-ident>`]; `@page <name>` selection keyed off the break-triggering box's page).

- **2026-06-15 — Cycle 7 (named pages) DONE** (branch `phase-3-multi-page-cycles-7-8-fontdedup`). `@page <name>` margin boxes select per page by the page's NAME. The `page` property (CSS Page 3 §3.4, `auto | <custom-ident>`) is DROPPED by AngleSharp, so it joined `CssPreprocessor.KnownDroppedProperties` (recovered verbatim, read RAW from the cascade — not yet a registered first-class property, so no `@supports`/validation: a documented follow-up). `AtPageRules.ResolveUsedPageName(element, cascade)` computes a box's §3.4 USED page name by walking ancestors for the nearest non-`auto` `page` (modelling `auto` → parent). `PageSelectorContext` gained `AssignedPageName`; `MatchTier`/`MatchSelector` match a bare `<custom-ident>` selector at TIER 3 (> `:first`/`:blank` tier 2 — CSS Page 3 §3.1 named > pseudo), via a new `IsBarePageName` validator (rejects pseudos, compounds, `auto`/CSS-wide keywords, leading-digit). The driver computes each page's name from `FirstSourceElement(bodyFragments)` — the page's first CONTENT fragment, SKIPPING the `<html>`/`<body>` wrappers (they span every page with `page: auto`, so the first fragment in the list is the body continuation, which would wrongly name the page ""; `ResolveUsedPageName` still walks back up through them for an inherited name) — and threads it into `PageSelectorContext`; the `marginBoxCache` key gained the name. `ResolveAll` (the structural/prefetch union) adds a context per `AtPageRules.DeclaredPageNames` (a named box never matches an anonymous representative context). Tests: `AtPageRulesTests` (named `MatchTier` incl. named > `:first`, case-sensitivity, reject non-names/compounds; `ResolveUsedPageName` ancestor-walk + null/blank), `AtPageMarginBoxResolverTests` (`Resolve(named)`, named > `:first`, `ResolveAll` includes named, `DeclaredPageNames`), facade `Multi_page_named_page_selects_its_margin_box` (a `page: chapter` block makes its page paint `@page chapter`'s header — "AA" vs "AB"). DEFERRED: per-page GEOMETRY for named pages (margins/size — like `:left`/`:right`, needs iterative layout); COMPOUND selectors (`chapter:first` — `MatchTier` defers; `DeclaredPageNames` still collects the leading name for the union); registering `page` as a first-class property; the break-triggering-box heuristic (first content box skipping wrappers) is an approximation for a spanning intermediate container. Gates: **6816 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning · AOT/JIT parity verified**.
  - **Next:** cycle 8 — corpus + golden verification (real multi-page invoices/reports through the driver; table `<thead>`/`<tfoot>` repeat + multicol/flex/grid continuations); then the font-dedup-across-pages follow-up.

- **2026-06-15 — Cycle 8 (corpus + golden verification) DONE** (same branch). A multi-page composition golden — `HtmlPdfConvertTests.Multi_page_composition_paginates_with_running_header_footer_counter_and_named_page` — drives a three-section report through the real driver and asserts the whole arc COMPOSES: block content paginates at section boundaries (cycles 1–3), every page paints a running header from `string-set` carry-forward (cycle 5) + a `counter(page)`/`counter(pages)` footer (cycle 4), the middle section is a `page: ref` NAMED page that swaps in `@page ref`'s header (cycles 6–7), the byte output is DETERMINISTIC (same input → identical bytes, CLAUDE.md §4), and NO `PDF-CONTENT-OVERFLOW-TRUNCATED-001` fires (block content flows). **SURFACED (the cycle-8 "exercise the layout modes" goal):** a direct probe confirmed flex/grid/table/multicol do NOT paginate through the driver — a tall `display: flex`/`grid`/`table`/multicol container lays ALL its children on the current page (flex/multicol overflow-truncate, table doesn't split rows, `<thead>`/`<tfoot>` don't repeat). The nested-container fragmentation (cycles 1–2) is GATED to block-flow children (`IsBlockFlowContainerOwnedByBlockLayouter`); the non-block layouters have continuation types (`Table`/`Flex`/`Grid`/`Multicol` `Continuation`) + unit tests but don't propagate a continuation through the fragmentainer the way `BlockLayouter` now does. Wiring each into the driver loop is its OWN substantial layout task — documented as remaining (deferrals.md), NOT fixed in this verification cycle. Gates: **6817 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning · AOT/JIT parity verified**. **⚠ CORRECTION (see the non-block-pagination audit entry below): this "do NOT paginate" reading was a FALSE NEGATIVE.** The probe used explicit cell/item heights, which collapse to ~one text-line tall, so its test containers never exceeded one page — TABLE row-split, `<thead>`/`<tfoot>` repeat, MULTICOL, and GRID-with-`grid-template-rows` DO paginate through the (continuation-agnostic) driver. The genuine gaps were FLEX-COLUMN (now done) + GRID-with-implicit-rows.
  - **Next:** the font-dedup-across-pages follow-up (text is subset PER PAGE; a 2-pass paint would subset/embed a shared font once — size/efficiency, byte-changing for multi-page, not correctness); then the non-block-mode pagination tasks (flex/grid/table/multicol continuations through the driver).

- **2026-06-15 — Post-PR-#179 review (cycles 7 + 8 + font-dedup; 2 P1 + 1 P2 + 2 P3, all addressed; same bundled PR).** **(P1 — named-page FORCED BREAK):** named pages assigned the page name AFTER layout, so a named section that FIT the current page never started a new page (`@page <name>` never applied). Per CSS Page 3 §3.4 a change in the used `page` value forces a page break. Now `Box.PageName` is computed at BUILD time (`AtPageRules.ResolveUsedPageName` onto a new `Box.PageName` init-prop via `Box.ForElement`), and `BlockLayouter.EmitBlockSubtreeRecursive` FORCES a break (returns a `BlockContinuation` at the child) before a block-flow child whose `PageName` differs from the preceding block-flow child's — reusing the cycle-1 nested break path + its forward-progress guard (so a named child that STARTS a page just names it, no spurious break). The driver reads each page's name from its first content box's `Box.PageName` (`FirstContentPageName`, replacing the post-layout cascade walk). Test: `Named_page_forces_a_break_even_when_the_content_fits` (a 100px intro + a 100px `page: chapter` section — both fit one page — split, page 2 paints `@page chapter`). **(P1 — `page` validation):** `ResolveUsedPageName` now treats CSS-wide keywords (`inherit`/`initial`/`unset`/`revert`) + INVALID raw values (`-1`, `123`) as "no name here" (the walk continues to the parent — `inherit` → parent's value, `initial`/`unset`/invalid → `auto` → parent), instead of returning them as literal page names; `IsBarePageName` rejects a leading `-` before a digit (`-1` isn't a valid ident, CSS Syntax §4.3.11). **(P2 — `Finish` cancellation):** the document-scoped text-finish pass (font subset/embed + per-page replay) now takes the `CancellationToken` + checks it before each font build, each page replay, and every 256 draws. **(P3 — empty page draws):** `TextPaintSession.CollectPage` skips storing a page with no draws (background/transparent/`font-size:0` pages) — no retained empty list. **(P3 — stale docs):** refreshed the `AtPageRules` class + `MatchTier` docs that still called named pages deferred. Gates: **6835 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning · AOT/JIT parity verified**.

- **2026-06-15 — Font-dedup-across-pages follow-up DONE** (same branch). Text was subset + embedded PER PAGE (each `TextPainter.PaintText` call built its own subset), so a font shared by N pages was embedded N times — a size bloat. Now a document-scoped `TextPainter.TextPaintSession` collects EVERY page's glyphs first (one HarfBuzz shaping pass, accumulated into one shared per-font glyph set in first-seen-across-pages order), then `Finish` subsets + embeds each font ONCE from the cross-page UNION and replays every page's draws (`AddFont` per page references the one shared embedded object). Because `PdfDocument.RegisterFont` already dedups identical embedded subsets by content key, subsetting the union once yields ONE font object. The driver creates the session before PHASE B, `CollectPage`s inside the per-page loop (after the bg/border/image/margin painting, preserving the text-over-backgrounds layering), and `Finish`es after. **Single-page output is BYTE-IDENTICAL** (a one-page session subsets that page's glyphs exactly as before — verified by AOT/JIT parity + the 30 snapshot + 97 realdoc tests); multi-page bytes shrink (one embedded font) and stay deterministic (first-seen font order + sorted union). `PaintText` is kept as a single-page wrapper over the session. Test: `Multi_page_shares_one_embedded_font_across_pages` (a two-page doc with a per-page `counter(page)` footer — page 1 "A", page 2 "B" — now has ONE `/FontFile2`, was two). FURTHER (deferred): the per-page subset BUILD still runs once (good), but a font whose glyphs differ wildly per page embeds the full union on every page's resource dict (the object is shared; only the resource reference differs — already optimal). Gates: **6818 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning · AOT/JIT parity verified**.

- **2026-06-15 — Non-block-pagination audit + FLEX-COLUMN pagination DONE** (branch `phase-3-flex-column-pagination`). **The cycle-8 "flex/grid/table/multicol don't paginate" finding was a FALSE NEGATIVE.** An empirical re-audit (probe driving genuinely page-exceeding content through the facade) found the cycle-8 probe had used explicit cell/item heights, which collapse to ~one text-line tall (a `<td><div style="height:200px">` renders short; flex/grid auto-height items collapse to bs=0), so its test containers never exceeded one page — it read "doesn't paginate" when the wiring was present. **What actually already works (now pinned with facade tests in `HtmlPdfConvertTests`):** TABLE row-split (non-lossy), `<thead>`/`<tfoot>` REPEAT per page, MULTICOL flow, and GRID with explicit `grid-template-rows` — each propagates its `Table`/`Multicol`/`Grid` continuation through `BlockLayouter`'s dispatch (exactly like `BlockContinuation`); the driver loop is continuation-type-agnostic, so they paginate end-to-end. **FLEX-COLUMN — the first genuine gap — DONE:** a tall `flex-direction: column` + `nowrap` container now splits at flex-ITEM boundaries down the main = block axis. `FlexLayouter` gained a per-item main-axis cut (emit items `[resumeItemIndex, splitItemIndex)` re-anchored to the page content-block-start; the first item on a page always commits per CSS Fragmentation L3 §4.4 forward-progress) carried by a new `FlexContinuation.ItemIndex`; `IsPaginatablePerStyle` now accepts column-nowrap. Crucially the split uses a NATURAL-size / PAGE-BUDGET **dual-input** (a new `ConfigureEmission(pageBlockBudget:)` + `DispatchFlexInner(pageBlockBudget:)`, mirroring `DispatchGridInner`): `containerMainSize` (flex resolution) stays the natural content extent so items keep their sizes, while the clamped page-remaining drives the cut — without it, clamping the column main-size would make flex-SHRINK fight pagination. `BlockLayouter`'s recursion flex dispatch captures the pre-clamp authored border-box block size + passes natural-as-contentBlockSize + clamped-as-budget for column (the wrapper fragment still paints clamped). Tests: 4 `FlexLayouterTests` (split-at-item-boundary against the budget, resume re-anchored, first-item-taller-than-page force-overflow, pagination-off atomic baseline) + facade `Flex_column_paginates_at_item_boundaries_without_loss` (12×200px column → ≥2 pages, all 12 item background fills present, no overflow). **REMAINING (documented, deferrals.md):** GRID with IMPLICIT rows (`grid-auto-rows` — `PreMeasureGridRowExtent` only sums template rows, so the wrapper stays chrome-height + never overflows); flex item CONTENT layout (`FlexLayouter` emits item box geometry but not inner text/children — orthogonal to pagination); row-flex per-item split + column-reverse/wrap (atomic). Gates: **6845 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning Release · AOT/JIT parity verified** (single-page output byte-identical — column pagination only activates for paginating column flex).
  - **Post-PR-#180 review (3 findings + 5 Copilot, all addressed; same PR).** **(P1, real bug)** the column dual-input was wired only at the RECURSIVE dispatch, not the OUTER one — a ROOT/body-level `display:flex;flex-direction:column` (e.g. `<body style="display:flex;flex-direction:column">`) routes through the outer dispatch, which clamped the column main-size to the page budget → flex-shrink collapsed items to fit → AllDone (one page) instead of splitting. Fixed by mirroring the recursive path at the outer site (`outerFlexAuthoredBorderBoxBlockSize` + `outerFlexColumnPaginating` → authored-as-sizing + clamped-as-`pageBlockBudget`). Tests: `Column_flex_as_root_direct_child_paginates_without_shrinking_items` (item heights stay 200, not shrunk to ~125) + facade `Flex_column_on_the_root_child_paginates_via_the_outer_dispatch`. **(P2)** the split flex wrapper kept the CLAMPED budget size instead of the emitted item extent (blank trailing space + over-advanced cursor pushing siblings too low, esp. final resume pages where remaining items < budget). Fixed with `FlexLayouter.LastEmittedBlockExtent` + a `DispatchFlexInner` out-param → both flex dispatch sites resize the wrapper via `UpdateFragmentBlockSize` + recompute the cursor advance from emitted-extent + chrome (mirrors grid's F2 resize; gated to column — row-wrap wrapper-resize stays the cycle-4f deferral). Tests: `Column_flex_split_wrapper_resizes_to_emitted_item_extent` + `..._final_resume_page_resizes_wrapper_and_places_sibling_tightly`. **(P3)** refreshed stale "non-block doesn't paginate" docs (this doc's cycle-8 entry + phased table; `FlexLayouter.IsPaginatablePerStyle`'s + the recursion clamp's comments still calling column ineligible) + added `Column_flex_pagination_splits_by_sorted_order_not_dom_index` pinning `ItemIndex` as a `order`-sorted position (not DOM index). **(Copilot)** the 5 inline comments map onto P1 (outer dual-input ×2), P3 (stale comments ×2), and a PROGRESS.md L3 unmatched-`**` markdown fix. Gates: **6850 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning Release · AOT/JIT parity verified**.

- **2026-06-15 — Post-PR-#178 review (cycles 5b + 6; 2 P1 + 1 P3, all addressed; same bundled PR).** **(P1 — `ResolveAll` cross-selector suppression):** the structural/prefetch union fed ALL `@page` rules through one cascade, so a bare `@page { @top-center { content: none } }` could suppress an earlier `@page :left { @top-center { content: "L" } }` from the union — `hasMarginBoxes` then went false and the LEFT header never painted (and the box's background-image was never prefetched). Fixed: `ResolveAll` now resolves each of the new `AtPageRules.RepresentativeContexts` (the distinct {first+right, left, right} × {blank, non-blank} selector match-sets) and concatenates — the cascade is applied PER context, so a box renderable on SOME page survives. The unused `EnumerateAllPageRules` was removed. Tests: `ResolveAll_keeps_a_selector_box_a_bare_none_would_suppress` + facade `Multi_page_selector_margin_box_is_not_suppressed_by_a_bare_content_none` (only the left page paints). **(P1 — direct-heading `element()` bucketing):** the cycle-5b nearest-ancestor walk collapsed direct sibling running headings under `<body>` (and several headings inside one multi-page container) all to the container's first page — page 2 could show the document's LAST heading instead of its own. Fixed: `CollectPerPage` now derives a running occurrence's page from DOCUMENT ORDER — `IndexDocumentOrder` numbers the DOM, `ResolveRunningPage` buckets the (removed-from-flow) running element to the page of the next rendered element FOLLOWING it WITHIN its containing block (the content it heads; binary-searched), falling back to the nearest rendered ancestor's page (a trailing heading) then page 0. Tests: `CollectPerPage_buckets_direct_sibling_running_headings_per_page`, `..._inside_one_multi_page_container_per_page`, `..._trailing_running_heading_falls_back_to_its_container_page`. **(P3 — per-page resolver re-walk):** the driver re-resolved the margin boxes + page-context declarations (× specificity tiers) from the stylesheets for EVERY page; now a `marginBoxCache` keyed by `(IsFirstPage, IsRightPage, IsBlank)` resolves each distinct context once (≤ a handful per document). Gates: **6805 unit / 5 skip · 30 LayoutSnapshots · 97 RealDocuments · 0-warning · AOT/JIT parity verified**.

- **2026-06-15 — Post-PR-#176/#177 review (5 review items + 4 Copilot, all addressed).** **(#176 P2 — empty-page suppression)** now terminates ONLY on the empty AllDone sentinel; an empty page that still carries a real continuation is preserved (future `@page :blank` / forced-parity / margin-box-only pages), guarded by the exact-page-count tests + the MaxPages cap. **(#177 P2 — inline `string-set`)** `CollectPerPage` resolves a setter element with no fragment (an inline `<span style="string-set:…">`) to its nearest rendered ANCESTOR's page (new `ResolvePage`), so inline assignments aren't dropped — GCPM `string-set` applies to all elements. **(#177 P3)** added exact-value unit tests (`CollectPerPage_distinguishes_first_and_last_assignment_on_one_page`, `..._buckets_an_inline_string_set_to_its_rendered_ancestors_page`) — semantic assertions, not glyph-inequality. **(#177 P1 — scoped per the reviewer's option)** cycle 5 is **cross-page NAMED STRINGS**; per-page `element()` running content (text + own styles / segments / containers) is **cycle 5b**, an explicit documented follow-up (for a single running element it's already correct on every page; multiple same-named running elements across pages is the deferred case — pinned by a skipped test). **(Copilot)** once-per-render margin bg-image diagnostic (was resetting per page); corrected the font-dedup + "CURRENT" doc wording; a no-per-page-allocation short-circuit in `CollectPerPage` when the document has no `string-set`.

---

## 1. Why now

The border-radius / outline / paged-media-decoration arc is complete. The remaining riders are all either large features in their own right or niche. The multi-page driver is the **highest-value remaining capability**: it is the core paged-media feature that makes NetPdf actually multi-page, and it unblocks, in one arc:

- **Cross-page running content** — `string-set` / `element()` persistence across pages (today single-page only).
- **Real `counter(page)` / `counter(pages)`** — `PdfRenderPipeline` currently hard-codes `(1, 1)` ([PdfRenderPipeline.cs:245](../../src/NetPdf/Rendering/PdfRenderPipeline.cs)).
- **`@page :left` / `:right` / `:blank`** selectors, and **named pages** (`page: foo` + `@page foo`).

---

## 2. What already exists (verified)

### 2.1 The `NetPdf.Paginate` engine — built, unit-tested, partially wired

| Type | Role | Wired today? |
|---|---|---|
| `IBreakResolver` / `BreakResolver` (greedy) / `OptimizingBreakResolver` (DP) | Break decisions; cost-minimizing window resolution | `BreakResolver` **active** (called from layouters); `OptimizingBreakResolver` built but not the primary path |
| `BreakOpportunity`, `BreakDecision`, `BreakAction`, `OptimizerResult` | Candidate breaks + decisions + DP output | active |
| `CostModel`, `Optimizer` | §penalty matrix + bounded 2-page Knuth-Plass DP | active (DP only via `OptimizingBreakResolver`) |
| `FragmentainerContext` (+ `.Clone()`) | Per-page mutable state; `PageIndex`, `TotalPages`, `NamedStrings`, `RemainingBlockSize` | active; `.Clone()` carries `NamedStrings`+`TotalPages` forward — **unused today** ([FragmentainerContext.cs:136](../../src/NetPdf.Paginate/FragmentainerContext.cs)) |
| `LayoutContinuation` (+ `Block`/`Inline`/`Table`/`Flex`/`Grid`/`Multicol` subtypes) | "Where to resume on the next page" | active; `LayouterState` field designed to carry nested state |
| `LayoutCheckpoint` (+ pool) | Atomic rewind snapshot | active inside `BlockLayouter` |
| `LayoutRetryCoordinator` | Bounded retry: Strict → DropAvoidInside → LastResort | built; the prototype driver uses it |
| `ILayouter.AttemptLayout(...)` | Layouter-facing contract | implemented by Block/Inline/Table/Flex/Grid/Multicol layouters |

**`BlockLayouter` already implements the resume path.** Its constructor accepts a `BlockContinuation` as `incomingContinuation`, validates it, and `AttemptLayoutInFlow` reads `incomingBlock = _incomingContinuation as BlockContinuation` and skips already-consumed children ([BlockLayouter.cs:458–491, 641](../../src/NetPdf.Layout/Layouters/BlockLayouter.cs)). The machinery to resume a page is present — it just only covers the root's direct children (see §3).

### 2.2 The driver-loop prototype (`wip-multi-page-driver-blocked`)

Commit `415f7f7`, branched off the long-superseded `b63351e`. It changed only `PdfRenderPipeline.cs` (+ a diagnostic rename + tests) — confirming the loop is a **pipeline-level** change. Its proven shape:

```
document + shared shaper allocated ONCE (fonts dedup across pages)
for (pageIndex = 0; ; pageIndex++):
    fresh ListFragmentSink                     // page-local coordinates
    BlockLayouter(incomingContinuation: continuation)
    LayoutRetryCoordinator.Run(...)            // Strict → … → LastResort
    PaintPage(document, mediaBox, sink.Fragments, …)   // bg/border → text
    if result.Outcome != PageComplete || result.Continuation is null: break
    continuation = result.Continuation
    if pageIndex+1 >= MaxPages (20_000): clip + break   // forward-progress backstop
if document.Pages.Count == 0: AddPage(mediaBox)         // never page-less
if clipped: emit narrowed PDF-CONTENT-OVERFLOW-TRUNCATED-001 (inline overflow / cap only)
```

It is **stale** (predates Task 21's `@page` margin boxes entirely, so it does **not** paint per-page headers/footers or feed real counters) and built on an ancient base — it is a **reference, not a cherry-pick**. We rebuild the loop on `main` and integrate the now-existing margin-box pass.

### 2.3 Paged-media cascade — `:first` works; the rest is gated

- `AtPageRules.EnumeratePageRulesWithMediaInfo` yields **bare** then **`:first`** rules; resolvers apply last-wins so `:first` overrides the bare page by specificity ([AtPageRules.cs](../../src/NetPdf.Css/PagedMedia/AtPageRules.cs)).
- `ClassifyPageSelector` recognizes `:first` (→ `PageSelectorKind.First`); **everything else → `Deferred`** (recognized but not applied).
- A `PageParity { Any, Left, Right, Recto, Verso }` enum already exists in `BreakOpportunity.cs`.
- **No handling of the `page` CSS property exists** (named pages need it).

### 2.4 Running content + counters — collected once, for the whole document

- `MarginContentCollector.Collect(root, cascade)` walks the document **once** and returns a single `MarginContentContext` (`Named`/`Running` + their `First` variants) — last-wins / first-occurrence over the **entire document**, not per page.
- `CssContentList.PageCounters(page, pages)` is a 1-based pair with a `Page ≤ Pages` guard; the pipeline passes `(1, 1)`.

---

## 3. The blocker (precise)

From [deferrals.md#layout-to-pdf-pipeline](../deferrals.md), confirmed by a direct-layouter probe:

> `BlockLayouter` fragments only the layout **root's DIRECT children** across pages; a **nested** block container lays out **all** its children on the current page.
>
> Probe: `root → wrapper → 6×200px` on a 500px page (Strict) → page 0 = `PageComplete` with **all 7 fragments** (1200px, overflowing); page 1 = `AllDone` with **0 fragments**.

The recursive emit (`EmitBlockSubtreeRecursive`) walks nested children in a silent inner loop with **no break consultation** — nested subtrees are treated as **atomic**: a subtree that doesn't fit is pushed wholly to the next page or force-overflowed, never **split**. Since every facade document nests under `html → body`, the first nesting level already defeats pagination.

**This is the gating layout task.** The driver loop, counters, and `@page` selectors are all comparatively mechanical; none of them matter until a nested container can split.

---

## 4. Proposed design

### 4.1 Layout core — nested-container fragmentation (the hard part)

**Approach: split at child boundaries, carry a nested `BlockContinuation` chain.** (Recommended by the layout exploration; aligns with the existing `BlockContinuation.LayouterState` design, which was built to carry nested layouter state.)

Concretely, in `BlockLayouter.EmitBlockSubtreeRecursive`:

1. **Thread the budget + resolver into the recursion.** Today `propagatingResolver` / `propagatingFragmentainer` exist as parameters but default to `null`. Always pass them, plus the remaining block-size.
2. **Consult the resolver after each nested child.** Call `resolver.ConsiderBreakAt(opportunity, fragmentainer)` at each nested block boundary (the same call the top-level loop already makes), honoring `BreakHere` / `Rewind`.
3. **Return a nested `BlockContinuation` on break.** When the recursion breaks mid-subtree, return `BlockContinuation(ResumeAtChild: nextIdx, LayouterState: <deeper continuation>)`. The parent wraps it; the chain walks back down on resume.
4. **Capture/restore at nested depth.** Extend checkpoint capture to store `(depth, nestedChildIdx, margin-collapse frontier, fragment cursor)` so a nested break can rewind without re-emitting prior siblings. Mirror the top-level margin-collapse reset at the new page boundary (CSS Fragmentation L3 §6.1 — margins don't collapse across page breaks).

**Deliberately *out* of the first cut** (force-overflow + diagnostic, as today):
- **Line-level splitting inside a block** — a single block taller than a page stays atomic (its lines don't split across the boundary). This matches the current per-block atomic-line model; line splitting is a later cycle via `InlineContinuation` in `BlockContinuation.LayouterState`.
- **Float cross-page continuation** — already a separate deferral (`float-continuation-propagation`); stays force-truncated with `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001`.

**Estimated scope:** ~550–750 lines in `BlockLayouter.cs` + minor `LayoutContinuation`/checkpoint extensions, per the exploration. This is the multi-cycle core and should be broken into its own sub-cycles (see §5).

### 4.2 The driver loop

Rebuild the prototype loop on `main` (§2.2), with three modernizations:

- **Integrate the per-page margin-box pass.** Today's single-shot margin-box block ([PdfRenderPipeline.cs:229–292](../../src/NetPdf/Rendering/PdfRenderPipeline.cs)) moves **inside** the loop (or into `PaintPage`), re-resolved per page with that page's counters, selector context (§4.3), and running content (§4.4).
- **Feed real counters.** `new PageCounters(page: pageIndex + 1, pages: <total>)` — see §4.5 for how `<total>` is known.
- **Keep determinism + the perf gate.** Fonts dedup across pages via the shared `PdfDocument` + shaper (already in the prototype). Memory stays linear in page count (we accumulate per-page fragment lists; CLAUDE.md perf gate §8).

### 4.3 Paged-media page context (`:left`/`:right`/`:blank`/named)

Introduce a per-page selector context and thread it through the `@page` resolvers:

```csharp
readonly record struct PageSelectorContext(
    int PageIndex,            // 0-based
    PageParity Parity,        // from page index + the document's first-page side
    bool IsBlank,             // page emitted no body fragments
    string? AssignedPageName) // from the `page` property on the break-triggering box
{
    bool IsFirstPage => PageIndex == 0;
}
```

- Generalize `ClassifyPageSelector` → a `MatchesPageContext(prelude, ctx)` that handles `:first` / `:left` / `:right` / `:blank` / named idents / simple compounds (`chapter:first`), feeding `AtPageMarginResolver` / `AtPageSizeResolver` / `AtPageMarginBoxResolver`.
- Add the **`page` CSS property** (CSS Page 3 §3.4) to the cascade (inherited `<custom-ident>` | `auto`); capture the winner from the box that triggers each page break.
- **Parity** derives from page index (page 0 = recto/right by default for LTR); the spec's named-page + RTL axis flips ride along.

This sub-arc is **independent of §4.1** and can land after the fragmentation+driver capability is real.

### 4.4 Cross-page running-content persistence

Replace whole-document collection with **per-page first/last snapshots**. Two viable shapes:

- **(A) Tie collection to fragment emission.** As `BlockLayouter` emits each box's fragment, record `string-set` assignments / `position: running()` elements **in emission order**, tagged by page. At each page boundary, the "first on page" and "last on page (exit value)" are derivable; `string(name, start)` carries the prior page's exit value forward.
- **(B) Post-layout pass over per-page fragment lists.** Since the driver already accumulates a fragment list per page (§4.5), a second pass maps each named-string / running-element occurrence to its page and computes per-page first/last there — no layout-engine change.

**(B) is lower-risk** (no `BlockLayouter` change) and is the recommended first cut; `start` / `first-except` become resolvable once per-page first/last exists. `FragmentainerContext.NamedStrings` + `.Clone()` already carry the cross-page table forward if we later prefer (A).

### 4.5 `counter(pages)` total — layout-all-then-paint

`counter(pages)` on page 1's footer needs the total **before** page 1 is painted. Recommended: **separate layout from paint across the whole document** (the prototype already separates them per page):

```
PHASE A (layout):  loop AttemptLayout over continuations → accumulate
                   List<PagePlan>{ fragments, selectorContext, runningSnapshot }
total = pages.Count                          // now known
PHASE B (paint):   for each PagePlan: PaintPage(... PageCounters(i+1, total) ...)
```

This keeps a **single layout pass** (no double cost), makes `counter(pages)` exact, and gives §4.4(B) its per-page fragment lists for free. Memory is linear in page count (perf gate-compliant). The alternative — a measure-only pre-pass then a render pass — doubles layout cost and is **not** recommended.

---

## 5. Phased task breakdown (one PR per cycle, per the review cadence)

Each cycle ships as a PR → your numbered review → I implement valid items with unit+integration tests → Copilot pass → PROGRESS.md note → squash-merge. Ordered so value lands early and the risky core is de-risked first.

| # | Cycle | Scope | Depends on |
|---|---|---|---|
| **0 ✅** | **Blocker confirmed + characterization test** | DONE (2026-06-14). Green characterization test for the `root→wrapper→6×200` case directly on `BlockLayouter.AttemptLayout`; the *desired* split is pinned by the existing skipped `Cycle2d` test. Proves the gap on `main`, defines "done". | — |
| **1 ✅** | **Nested-container fragmentation** | DONE (2026-06-14). `EmitBlockSubtreeRecursive` consults the resolver before each plain block-flow child + returns a `BlockContinuation` on block-axis overflow; reuses the existing `LayouterState` chain + caller propagation; greedy resolver, child-boundary granularity. Probe rewritten + `Cycle2d` un-skipped, both green. | 0 |
| **2 ✅** | **Arbitrary depth** | DONE — came free with cycle 1 (the recursion self-threads the resolver + the caller chains the continuation, no per-level work). Depth-2 test green. | 1 |
| **3 ✅** | **Driver loop on `main`** | DONE (2026-06-14). `RenderAsync` layout-all-then-paint loop; skips the trailing empty resume page; narrowed `PDF-CONTENT-OVERFLOW-TRUNCATED-001` to inline-overflow/cap. Real multi-page PDFs. | 2 |
| **4 ✅** | **Real page counters + per-page margin boxes** | DONE (2026-06-14). Margin-box pass per page; `PageCounters(i+1, total)` — `counter(page)`/`counter(pages)` correct across pages. | 3 |
| **5 ✅** | **Cross-page running content (named strings)** | DONE (2026-06-15, + PR review). `MarginContentCollector.CollectPerPage` + an element→page map (inline setters resolved to their nearest rendered ancestor) → per-page `string(name)` first/last with carry-forward. | 4 |
| **5b ✅** | **Cross-page `element()` running content** | DONE (2026-06-15). `MarginContentCollector.CollectPerPage` buckets each `position: running()` occurrence (its text + own style + segments + container bands, carried together as one `RunningOccurrence`) by its element's nearest-rendered-ancestor page + carries forward until re-set, mirroring cycle 5's named strings; page-0 fallback for an all-running body; single page short-circuits to the whole-doc context. | 5 |
| **6 ✅** | **`@page :left` / `:right` / `:blank`** | DONE (2026-06-15). `AtPageRules.PageSelectorContext` (first + LTR parity + blank) + `MatchTier` (the specificity-ordered generalization of `ClassifyPageSelector`) → context-aware `AtPageMarginBoxResolver.Resolve(ctx)` / `PageContextDeclarations(ctx)` + `ResolveAll` (union for prefetch), re-resolved PER PAGE in the driver so margin boxes honor each page's selectors. Per-page GEOMETRY (margins/size differing by `:left`/`:right`) deferred — needs iterative layout. | 3 |
| **7 ✅** | **Named pages** | DONE (2026-06-15). The `page` property (recovered via `CssPreprocessor` — AngleSharp drops it — read raw, ancestor-walked for the §3.4 used value by `AtPageRules.ResolveUsedPageName`); `@page <name>` margin-box selection — `MatchTier` matches a bare `<custom-ident>` at tier 3 (> `:first`/`:blank`), `PageSelectorContext.AssignedPageName` from the page's first CONTENT box (skipping the `<html>`/`<body>` wrappers), `ResolveAll` unions a context per `DeclaredPageNames`. Per-page GEOMETRY + compounds (`chapter:first`) + registering `page` as a first-class property deferred. | 6 |
| **8 ✅** | **Corpus + golden verification** | DONE (2026-06-15). A multi-page composition golden (`Multi_page_composition_…`) verifies cycles 1–7 compose end-to-end — block pagination + running header (`string-set` carry-forward) + `counter(page)`/`counter(pages)` footer + a named page — with deterministic bytes + no overflow truncation. ~~SURFACED: flex/grid/table/multicol DON'T paginate~~ **CORRECTED (non-block-pagination audit): that was a FALSE NEGATIVE from collapsing explicit cell/item heights — table/multicol/grid-with-`grid-template-rows` DO paginate (now pinned with facade tests); flex-column is now done; grid-with-implicit-rows is the documented follow-up.** | 4–7 |

Cycles 6–7 are independent of 4–5 and can interleave. Cycle 8 gave the table/flex/grid/multicol multi-page paths their **first real end-to-end exercise** — and the non-block-pagination audit (the 2026-06-15 entry above) corrected cycle-8's false-negative reading: table/multicol/grid-with-`grid-template-rows` already paginate through the driver, flex-column was wired, and grid-with-implicit-rows remains the documented follow-up.

---

## 6. Cross-cutting constraints

- **Determinism (CLAUDE.md §4, [determinism.md](determinism.md)).** Same input → same bytes. Fonts dedup via the shared document; the AOT/JIT parity pin (`scripts/aot-parity.sh`) must stay byte-identical or be re-pinned with rationale. No PRNG / wall-clock.
- **AOT-clean (§3).** No reflection in the new driver/fragmentation paths.
- **Performance gates (§8).** 20-page report ≤ 1.5 s p50; **memory linear in page count** — layout-all-then-paint accumulates per-page fragment lists, which is linear. No process spawning.
- **Clean-room (§1).** Fragmentation + break algorithms from CSS Fragmentation L3 / CSS Page 3, not from reading other engines.
- **Diagnostics (§7).** Reuse `PAGINATION-FORCED-OVERFLOW-001` / `PAGINATION-OPTIMIZER-FALLBACK-001` / the per-layouter forced-overflow codes; the driver narrows `PDF-CONTENT-OVERFLOW-TRUNCATED-001` to "inline-overflow / page-cap" rather than "content past page 1 dropped."

## 7. Testing strategy

Per [memory: testing discipline] every cycle ships **unit + integration**:
- **Unit** — direct `BlockLayouter.AttemptLayout` probes (fragment counts / continuation shape per page); `PageSelectorContext` matching; per-page first/last running-content resolution; `PageCounters` wiring.
- **Integration** — facade `HtmlPdf.Convert` → multi-page PDF: page count, per-page header/footer text (string-searchable uncompressed streams, per [memory: NetPdf PDF-output test techniques]), `counter(page)`/`counter(pages)` strings, deterministic bytes.
- **Golden** — `tests/NetPdf.PaginationGolden` (phase-3 doc Task 27): input HTML → expected break sequence.

---

## 8. Open scope decisions (need your sign-off)

1. **Arc shape.** Land the **capability** first (cycles 0–5: fragmentation + driver + counters + cross-page running content), then `@page` selectors + named pages (6–7) as a follow-on? Or bundle all of it before declaring multi-page "done"?
2. **`counter(pages)` total.** Confirm **layout-all-then-paint** (§4.5) over a measure-pre-pass. (Recommended: layout-all-then-paint — single layout pass, exact total.)
3. **Break resolver.** Ship the **greedy `BreakResolver`** first (correctness), switch to `OptimizingBreakResolver` (DP cost model) as a later drop-in via the same `IBreakResolver` seam? Or wire DP from the start?
4. **First-cut fragmentation depth.** Child-boundary splitting only (a single over-tall block force-overflows; **no line splitting**) for the first cut, line splitting later? (Recommended: yes — matches the current atomic-line model.)
5. **Table/flex/grid/multicol multi-page.** Treat their multi-page paths as "should work via existing continuation chains — verify in cycle 8, fix what breaks," rather than a guaranteed-complete deliverable of this arc?

## 9. Out of scope (this arc)

- Line-level splitting within a block (later cycle).
- Float cross-page continuation (`float-continuation-propagation` deferral).
- Intra-row table-cell splitting, intra-row grid-item splitting (row-atomic only, per existing deferrals).
- RTL `@page :left`/`:right` axis flips beyond the basic parity mapping.
- Bundled fallback font for default-path text determinism (`cycle 5b` — needs a legal/dependency-dossier entry; orthogonal).
