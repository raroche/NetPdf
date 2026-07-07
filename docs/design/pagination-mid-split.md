# Handoff — pagination "mid-split" (cycle-2d) + the 03 density fix

**Status:** ✅ **IMPLEMENTED** (branch `pagination-mid-split`). **Repo state at start:** `main` @ `4d151f4` (batch-3 / PR #280 merged). **Goal of the PR this doc describes:** make a block-flow subtree that doesn't fit the *remaining* page **start on the current page and break between its children** ("mid-split"), instead of moving *wholly* to the next page. This closes the `03-itinerary` over-pagination (renders 4 pages where ~2 suffice) and the general density waste.

---

## ✅ RESOLUTION (implemented)

**Result:** real `03-itinerary` **4 → 2 pages** (fills `[0.44,0.99,0.15,0.08]` → `[0.87,0.79]`), full content preserved (body = 98 runs both ways; the run-count delta was only per-page running header/footer boxes). All repo golden suites are **byte-identical** (the shape only occurs in deeply-nested multi-child overflow, absent from the repo corpus) — **no re-baseline was needed**. Full gate green: build 0-warn · UnitTests 8502 · LayoutSnapshots/PaginationGolden/RenderingCorpus/RealDocuments byte-identical · fuzz 0 findings · AOT parity verified.

**Where the atomic decision lived (Step 2 answer):** NOT the outer-loop line-2453 chunk site — it's the **recursion** `EmitBlockSubtreeRecursive`'s nested break-check (`BlockLayouter.cs`, the `nestedDecision = propagatingResolver.ConsiderBreakAt(...)` gated on `IsBlockFlowContainerOwnedByBlockLayouter(child)`). The timeline's whole-subtree extent (1194px) > remaining (612px) → `BreakHere` → moved wholly.

**The three changes (all gated on `MidSplitEnabled`, default ON; env `NETPDF_MIDSPLIT=0` = escape hatch):**
1. **Enter instead of move-wholly** — at that nested break-check, for an eligible container (`IsEnterAndSplitEligible`: block-flow, ≥2 in-flow block children, not `break-inside:avoid`) whose whole subtree overflows the remaining page but whose FIRST in-flow child fits, feed the resolver the first-child extent (`EstimateFirstInFlowChildExtent`) so it returns `Continue` and the container is entered.
2. **Flex child-boundary break** — a flex child that fits a FRESH page but not the remaining space breaks BEFORE it (move wholly to the next page) instead of force-overflowing off the page bottom (the pre-fix gap: a `.day` starting too near the page end couldn't internally paginate). Scoped to flex; grid/table keep their own deferral machinery.
3. **Resume cursor-advance fix** — a RESUMED block-flow container that completes on a page now advances the parent's cursor by what it ACTUALLY emitted this page (`_resumedContainerEmittedExtent`), not its full measured subtree extent (which counts prior-page children) — this is what let the trailing `.note` land on the same page instead of a lone final page.

**`EstimateFirstInFlowChildExtent` subtlety:** measuring a flex/grid child *standalone* hits the measure's opaque-non-block-flow path (chrome-only ≈ 0); it falls back to the container's reliably-folded subtree extent ÷ in-flow-child count when the direct measure returns ~0.

**Tests:** `tests/NetPdf.UnitTests/Rendering/PaginationMidSplitReproTests.cs` — faithful repro + density target (days 8/10/14 → 2/2/3 pages), content-preservation invariant (flex case), block-flow-child enter-split case, and `break-inside:avoid` guard.

**Still open (separate PRs):** §5 `.day .badge` abspos drop, and §6 pre-1.0.0 launch track.

---

> The visible defect on 03 — the footer overlapping the itinerary — is **already fixed and merged** (T1 in #280, auto-height row-flex sized to content). What remains is **density only** (too many pages), not correctness. Do not conflate the two.

---

## 1. What "mid-split" is (the problem)

The pagination engine currently commits a block-flow subtree **atomically**. For a block child that doesn't fit the space remaining on the current page it does one of:

- **move wholly** to the next page (`break-before`) — when the subtree fits a *full* page; or
- **force-overflow** — commit it anyway and overrun the page — when the subtree is taller than a *full* page (emits `PAGINATION-FORCED-OVERFLOW-001`).

What it does **not** do is the CSS-correct thing: **start the subtree on the current page and break between its children**, filling the trailing space, then continue the remaining children on the next page. That's the deferred "cycle-2d" work (see the comments in `BlockLayouter.cs` near the `MeasureSubtreeVisualBlockExtent` call — search `cycle 2c`, `cycle 2d`, `commits on one page`, `mid-split`).

### The 03 symptom (measured)
Real `03-itinerary` = 4 pages. Body-content distribution (running header ~y816 / footer ~y18 excluded):

| page | content | waste |
|---|---|---|
| 0 | header + hero + glance + **day 1 only** (splits after its toprow) | ~427px blank |
| 1 | days 2–7 (full) | ~15px |
| 2 | **day 8 only** | ~646px blank |
| 3 | the `.note` only | ~696px blank |

Header+hero+glance+8 days+note ≈ 1.3–1.5 pages of content → should be **~2 pages**.

### Two manifestations (found via a synthetic repro `.hdr` block + `.timeline` of N auto-height flex `.day` rows + `.note`)
- **`header=400px, 10 days`** → timeline moves **wholly** to page 1 (`[1, 50, 1]` BT text-blocks/page: header alone on p0, 10 days on p1, note on p2). The subtree fits a full page but not the ~600px remaining, so it break-before's.
- **`header=0, 14 days`** → timeline **overflows one page without splitting at all** (`[70, 1]`: 14 days on p0, note on p1). It exceeds a full page → force-overflow, no internal breaks.

⚠️ **The synthetic repro above is NOT yet faithful** — real 03's days DO split across pages (wastefully); the repro's don't split (they overflow). **Step 1 of the PR is to build a repro that reproduces the *real 03* behavior** (splits + wastes pages), or the fix will be measured against the wrong thing.

---

## 2. Precise findings + dead-ends (do not repeat)

- **Greedy resolver decision** — `src/NetPdf.Paginate/BreakResolver.cs`, `ConsiderBreakAt`: after the forced-break + suppress checks, it's literally `if (opportunity.ChunkBlockSize <= ctx.RemainingBlockSize) return Continue; else return BreakHere;`. So the whole behavior turns on **what chunk size is passed** for the subtree. (The `OptimizingBreakResolver` is cost-based but the production path uses the greedy one.)
- **Chunk computation (one site)** — `BlockLayouter.cs` ~`chunkForBreakCheck = Math.Max(marginBoxBlockSizeForCursor, visualBlockExtent)`, where `visualBlockExtent` derives from `effectiveBlockSize = Math.Max(borderBoxBlockSize, subtreeBlockExtent)` and `subtreeBlockExtent = MeasureSubtreeVisualBlockExtent(child, …)` (the **whole** subtree extent). So the resolver sees the *entire* timeline (~860–1200px) as one indivisible chunk.
- **DEAD-END #1** — a gated diagnostic (`NETPDF_MIDSPLIT`) that reduced `chunkForBreakCheck` at that site to the *first in-flow block child's* extent **never fired for the timeline**. Conclusion: **the timeline's move-wholly/overflow decision is emitted from a different branch** (not that `chunkForBreakCheck` site) — possibly the forced-overflow path, the recursion entry, or a measure-driven break at a different level. **Step 2 of the PR must pin the real site** (see below).
- **DEAD-END #2** — earlier, a `MeasureSubtreeVisualBlockExtentRecursive` change that folded the flex content extent into the *pagination measure* removed the 03 overlap but **over-paginated** (12% measure/emit mismatch) — that was the wrong layer; the shipped T1 fix drives sizing from the *emission* instead. Don't re-touch the measure for density.
- **Resume machinery exists** — `BlockContinuation.ResumeAtChild` / `ConsumedBlockSize` / `LayouterState` (`src/NetPdf.Paginate/LayoutContinuation.cs`), and the chain-peel resume in `BlockLayouter.cs` (`incomingBlock?.LayouterState` unwrap; `startChildIdx`/`ResumeAtChild`). It's exercised today for flex/grid/table/multicol continuations wrapped in `BlockContinuation.LayouterState`, and for whole-child resume of block-flow — but **plain block-flow mid-split in the "fits-a-page-but-not-remaining" case is not triggered**.

Useful env probes (all read `NETPDF_DBG=1`): add a `Console.Error.WriteLine` at the break-check and print `child` identity (e.g. gate on `child.Style.ReadLengthPxOrZero(PropertyId.BorderLeftWidth) > 0` to isolate `.day`s, or match a class marker), `fragmentainer.UsedBlockSize`, `fragmentainer.BlockSize`, the chunk, and `decision.Action`. Reliable page-count for probes: count `stream…endstream` blocks containing `" Tf"`. Per-page content span / BT-block count disambiguates "moved wholly" vs "overflowed" vs "split".

---

## 3. The single-PR plan

Do it in **one PR**, but build it in this order and keep it gated until the repro + a few goldens are verified.

### Step 1 — a faithful repro (test-first)
- Synthetic doc: a header/hero block consuming ~⅓–½ page, then a `.timeline` of **≥8 auto-height flex `.day` rows** (each `.day` = `display:flex` with a `flex:1 .body` holding a toprow + a multi-line list, ~120–150px), then a trailing `.note`. Tune content to ~1.3–1.5 pages.
- **It must reproduce the *real 03* shape** (days split across pages **and** pages are under-filled). Verify with the BT-blocks-per-page + per-page y-span probe (assert some page is <60% used while content remains).
- Encode the target as an assertion: `pageCount == ceil(totalContentPx / pageContentPx)` (no wasted page) **and** the note is the last content **and** the total text-run count is invariant vs. a single-tall-page render (no dropped/duplicated content).

### Step 2 — pin the atomic decision
- With `NETPDF_DBG`, find **exactly** where the timeline is moved wholly / force-overflowed (the line-2453 chunk site is *not* it — proven). Trace the outer child loop's `resolver.ConsiderBreakAt(opportunity, fragmentainer)` and the forced-overflow branch; print the chunk + remaining + `decision.Action` for the timeline box.
- Classify: does the timeline get `BreakHere` (moved wholly) or `Continue`-then-overflow? The `header=400` `[1,50,1]` evidence says **moved wholly**; confirm and record the exact file:line.

### Step 3 — enter-and-split
Gate all of this behind a temporary flag first (env or a `FeatureFlags` bool) so you can A/B and keep goldens byte-identical until you flip it.

- **Eligibility:** a block-flow container **owned by `BlockLayouter`** (`IsBlockFlowContainerOwnedByBlockLayouter`) with **≥2 in-flow block-level children**, **not** `break-inside: avoid`, whose subtree doesn't fit the remaining page **but whose first in-flow child *does* fit**. (Single-child / leaf blocks keep today's behavior — line-splitting is a separate mechanism.)
- **Enter instead of move-wholly:** for eligible containers, the break-check chunk must be the **first in-flow block child's extent** (the minimum unbreakable unit), not the whole subtree — so `ConsiderBreakAt` returns `Continue` and the container is entered. Measure the first child via `MeasureSubtreeVisualBlockExtent(firstChild, …, blockOffsetOnPage: used+topShift)`.
- **Break between children on overflow:** `EmitBlockSubtreeRecursive` must, when a child won't fit, stop and return `PageComplete(BlockContinuation(ResumeAtChild: thatChildIdx, ConsumedBlockSize: …, LayouterState: innerContinuation?))`. Confirm the outer loop propagates it (mirror the existing flex/grid propagation at the `nestedFlexResult is { Outcome: PageComplete }` sites) and that the resume page re-enters the container at `ResumeAtChild`.
- **Correctness invariants (each needs a test):**
  - **margin-collapse across the break** — CSS Fragmentation §: margins adjacent to a forced/unforced break are *truncated* to 0 at the page boundary. The last-emitted child's bottom margin and the first-resumed child's top margin must not produce a phantom gap or double margin. There's existing margin-collapse frontier state on the checkpoint (`prevBlockMarginEnd`, `hasAdjoiningBlockOnEntry`) — thread it through the resume.
  - **orphans/widows** — a container split at *child boundaries* doesn't split line boxes, so widows/orphans mostly matter only if a child is itself a paragraph that line-splits (already handled by the inline-only-block line-splitter). Don't regress it; add a test with an orphans/widows-constrained paragraph as the last child before the break.
  - **`break-inside: avoid`** on the container → **do not** enter-and-split (move wholly, today's behavior). `break-before/after: avoid`/`always`/`page` on children → honor at the child boundary (the metadata already flows via `ResolveChildBreakMetadata`).
  - **content preservation** — total `Tj`/`BT` count invariant vs. a single-tall-page render (no child dropped or duplicated across the split).
- Reuse, don't reinvent: `BlockContinuation.ResumeAtChild`, the chain-peel resume, `LayoutCheckpoint`, and the metadata resolver already exist.

### Step 4 — full golden re-baseline
- This **changes the break decision for every multi-block document** that doesn't fit the remaining space → expect **broad churn** in `NetPdf.LayoutSnapshots` (30), `NetPdf.PaginationGolden` (1), `NetPdf.RenderingCorpus` (36), `NetPdf.RealDocuments` (105).
- **Re-pin each changed golden ONLY after visually/structurally confirming the new output is correct** — pages fill better, nothing overlaps, nothing clips, no content lost. **Do not blindly re-baseline.** For each diff, confirm it's "content now fills the page" (fewer/denser pages), not a regression.
- Full gate: `dotnet build NetPdf.slnx -c Release` (0-warn) · full unit suite · all golden suites (re-pinned) · `bash scripts/aot-parity.sh` (or the AOT publish) · `dotnet run --project tests/NetPdf.Fuzz -c Release -- --smoke`.

---

## 4. Risk + approach

- This is the **highest-risk change in the codebase** — the crown-jewel fragmentation path. A subtle error (wrong resume index, margin double-count, dropped child, infinite re-entry) regresses many documents.
- **Incremental & flag-gated:** land the machinery behind a flag OFF (byte-identical), verify the repro + a handful of goldens with the flag ON, then flip the flag + re-baseline in the same PR. If the churn is unexpectedly large or any diff looks wrong, stop and reduce scope (e.g. only enter-and-split for containers taller than ~50% of a page, so tiny blocks still move wholly).
- **Invariant harness:** the strongest guard is "same content, different page distribution" — assert the total text-run count is unchanged across the fix for a set of docs.

---

## 5. Related, smaller follow-up (separate PR, or bundle): 03 `.day .badge` drop

Re-rendering 03 shows `LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001×8` — the eight day-number `.badge` circles are dropped. This is a **different** abspos case than batch-3 covered:

- `.day` is `display:flex; position:relative` (a flex **container**). `.badge` is a **block-level** `position:absolute` direct child of `.day`.
- Batch-3 T2 recorded geometry for abspos descendants inside a flex **item's** content buffer, and T5 handled inline abspos boxes swept into anonymous wrappers. Neither covers an abspos **child of the flex container itself**.
- **Fix direction:** record the flex **container's** border-box positioned geometry when `BlockLayouter` dispatches it to `FlexLayouter` (mirror the block-flow `RecordPositionedBoxGeometry` sites), so the badge's containing block (`.day`) resolves. Smaller scope than mid-split; verify against 03 (`×8 → 0`) and add a paint-proof test (abspos block child of a `position:relative` flex container renders at the container's box).

---

## 6. The other track (independent): pre-1.0.0 launch

Not engine work — maintainer/CI-box steps: version bump `0.9.0-rc1 → 1.0.0` (Directory.Build.props + build/version.json + CHANGELOG, guarded by `ReleaseVersionParityTests`) → tag `v1.0.0` → NuGet publish → public-repo flip; commit Linux Chrome reference PNGs + a linux-x64 benchmark baseline (flips the visual/benchmark gates enforcing); enable GitHub Pages (`NETPDF_PAGES_ENABLED=true`). NB **GitHub Actions is billing-blocked** on this repo (jobs fail in ~2s with no logs) — that must be cleared before the release workflow can run.

---

### Quick re-render recipe (to eyeball corpus output)
```
dotnet pack NetPdf.slnx -c Release -o /Users/rolandaroche/netpdf-localfeed
rm -rf ~/.nuget/packages/netpdf*/0.9.0-rc1
cd /Users/rolandaroche/Documents/repos/NetPdf-tester/NetPdfSmoke && rm -f invoices/*.pdf && rm -rf obj bin
dotnet run -c Release          # renders every invoices/*.html → PDF next to it, reports page counts + diagnostics
```
