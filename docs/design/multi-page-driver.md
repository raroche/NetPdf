# Design — the multi-page driver

**Status:** IN PROGRESS — **Cycles 0–2 done** (blocker confirmed + nested-container fragmentation at arbitrary depth). Next is cycle 3 (the pipeline driver loop). The §8 decisions were taken per the recommended defaults (#3 greedy resolver, #4 child-boundary granularity) — see the Progress log.
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

- **2026-06-14 — Cycle 0 (blocker confirmed) DONE.** Verified the nested-container fragmentation blocker still reproduces on `main` @ `c09198a` (the deferral's probe was measured on the long-superseded `b63351e`). Added a green characterization test, [`MultiPageDriver_blocker_nested_wrapper_lays_out_all_children_on_one_page`](../../tests/NetPdf.UnitTests/Phase3/BlockLayouterTests.cs), driving `root > wrapper(auto) > [6 × 200px]` on a 500px page directly through `BlockLayouter.AttemptLayout`. Confirmed exactly: page 0 returns `PageComplete` + a `BlockContinuation`, yet emits **all 7 fragments** (wrapper + 6 children, last bottom = 1200px — overflowing the 500px page); the resume page is `AllDone` with **0 fragments**. So nested content genuinely does not paginate.
  - **Key finding:** the *desired* post-fix behavior is **already pinned** by an existing skipped test, [`Cycle2d_oversized_subtree_splits_across_two_pages_at_inner_break`](../../tests/NetPdf.UnitTests/Phase3/BlockLayouterTests.cs) (`BlockLayouterTests.cs:4641`). Its skip reason already names the fix mechanism: a recursive continuation token (`BlockContinuation.NestedContinuation`) + break consultation **inside** `EmitBlockSubtreeRecursive` + recursive resume on retry. Cycle 1 implements that, un-skips `Cycle2d`, and flips the new characterization test to assert a clean child-boundary split.
  - **Next:** cycle 1 (§5) — pending the §8 scope sign-off (esp. decisions #3 break-resolver and #4 frag-depth, which shape cycle 1's "done").

- **2026-06-14 — Cycles 1 + 2 (nested-container fragmentation) DONE** (branch `phase-3-multi-page-cycle-1-nested-fragmentation`). `EmitBlockSubtreeRecursive` now consults the propagating resolver before each plain block-flow child and returns a `BlockContinuation` on block-axis overflow. The fix **reuses the existing `BlockContinuation.LayouterState` chain slot + the caller-side propagation** (both the normal walk and the forced-overflow path already wrapped + propagated a returned `BlockContinuation`), so the proposed dedicated `NestedContinuation` field was unnecessary — and it works at **arbitrary depth for free** (the recursion's self-call threads the resolver down; the planned cycle-2 work was already covered). §8 decisions taken per the recommended defaults: **greedy resolver (#3)**, **child-boundary granularity (#4)** — a single child taller than the page still force-overflows (no intra-child line split). Gating: forward-progress via `childCursor > 0` (oversized first child force-emits, never spins); **block-flow children only** (`IsBlockFlowContainerOwnedByBlockLayouter`) so flex/grid/table/multicol still paginate internally; floats excluded (their recursion sites omit the resolver); `position: fixed` excluded via `SuppressBlockPagination`. Tests: rewrote the cycle-0 probe → `MultiPageDriver_cycle1_nested_wrapper_children_split_across_pages_at_child_boundaries` (2/2/2 split), un-skipped `Cycle2d_oversized_subtree_splits_across_two_pages_at_inner_break`, added a depth-2 `..._works_at_arbitrary_depth`. One flex-pagination regression caught + fixed (the block-flow-only gate). Gates: **6761 unit / 5 skip · 30 snapshots · 97 realdocs · 0-warning Release · AOT/JIT parity verified**.
  - **Known artifacts → cycle 3.** (a) The forced-overflow continuation protocol signals one trailing EMPTY resume page (the resume-page subtree measure isn't resume-aware, so a wrapper re-measures as oversized once its remaining children fit); the driver skips painting it; resume-aware measure is a later refinement. (b) The wrapper fragment repeats full-size on each page it spans (per-page "partial" sizing deferred).
  - **Next:** cycle 3 — the pipeline-level multi-page driver loop (restore the prototype; **layout-all-then-paint** per §4.5; skip empty resume pages; real per-page counters + margin boxes).

- **2026-06-14 — Post-PR-#175 review (4 items, all addressed).** **(P1, real bug)** the nested forward-progress guard was the *signed* `childCursor > 0`, which mis-signals progress — a prior nested FLOAT emits a fragment without advancing `childCursor`, and a NEGATIVE-margin prior child can leave it ≤ 0 after real emission, both wrongly suppressing the break (collapsing nested pagination back to forced overflow). Replaced with a fragment-count baseline (`_sink.Cursor` captured at recursion entry; the break fires only once it advances) + regression tests for the float and negative-margin cases. **(P2)** the break now actually calls `propagatingResolver.ConsiderBreakAt` (it had hard-coded the overflow test): the recursion sets the fragmentainer's `UsedBlockSize` transiently to the child's block-start so the greedy resolver's `RemainingBlockSize` fit-check is correct, then restores it. **Deferred at nested boundaries** (documented in-code): checkpoint registration + `BreakAction.Rewind` (greedy never rewinds; the recursion holds no checkpoint) and break-before/-after/-inside metadata on the opportunity. **(P2-tests)** the multi-page tests now assert the EXACT page sequence + termination (final `AllDone`, null continuation, ≤ 1 trailing empty page), so a non-terminating "keeps returning empty PageComplete" regression can't slip through. **(P3)** added edge coverage: `SuppressBlockPagination` (no nested break), a first oversized child force-overflowing not looping, plus the P1 float/negative-margin tests; flex/grid/table/multicol-not-whole-box-deferred stays covered by `Task16_cycle4_closeout...`.

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
| **3** | **Driver loop on `main`** | Rebuild the prototype loop; **layout-all-then-paint** (§4.5); narrow `PDF-CONTENT-OVERFLOW-TRUNCATED-001` to inline-overflow/cap. Real multi-page PDFs for nested flat-block docs. | 2 |
| **4** | **Real page counters + per-page margin boxes** | Move the margin-box pass per page; feed `PageCounters(i+1, total)`. `counter(page)`/`counter(pages)` correct across pages. | 3 |
| **5** | **Cross-page running content** | Per-page first/last snapshots (§4.4 B); `string()` / `element()` persist + `start` / `first-except`. | 4 |
| **6** | **`@page :left` / `:right` / `:blank`** | `PageSelectorContext` + `MatchesPageContext`; parity from page index. | 3 |
| **7** | **Named pages** | The `page` property in the cascade; `@page <name>` selection. | 6 |
| **8** | **Corpus + golden verification** | Real multi-page invoices/reports; verify table `<thead>`/`<tfoot>` repeat, multicol/flex/grid continuations through a real loop; fix what the loop surfaces. | 4–7 |

Cycles 6–7 are independent of 4–5 and can interleave. Cycle 8 is where the "in scope per the phase doc" table/flex/grid/multicol multi-page paths get their **first real end-to-end exercise** (they have continuation types + unit tests but have never run through a document loop).

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
