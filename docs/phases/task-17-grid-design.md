# Task 17 — `GridLayouter` design

**Status:** Design draft revised post-PR-#88 review. Pending Roland review of v2 before cycle 1 starts.

**Spec target:** CSS Grid Layout L1 ([https://www.w3.org/TR/css-grid-1/](https://www.w3.org/TR/css-grid-1/)) for the v1 milestone. CSS Grid L2 adds subgrid which is out-of-scope (= post-v1 per `phase-3-layout-and-pagination.md`). **Masonry** (`grid-template-rows: masonry`) is a *separate* CSS Grid L3 draft — explicitly out-of-scope for this design.

**Phase-plan scope:**
- Task 17 (8 d): track-sizing algorithm
- Task 18 (5 d): placement + dense packing + areas

This doc covers Task 17 + Task 18 since they share the data model + box-builder + layouter scaffolding. The cycle plan splits the work across both tasks plus a new **cycle 0** for CSS infrastructure groundwork (added per PR-#88 review P1 #1).

---

## 1. Why this is the hardest layouter

CSS Grid's track-sizing algorithm (§11.4) is widely regarded as the most complex layout algorithm in CSS. It is a multi-pass iterative algorithm:

1. **Initialize Track Sizes** (§11.5.1) — set each track's base size + growth limit from its sizing function.
2. **Resolve Intrinsic Track Sizes** (§11.5.2-11.5.4) — distribute item min-content / max-content contributions across spanned tracks; multi-pass per growth-limit hits.
3. **Maximize Tracks** (§11.6) — when a definite container size is given, distribute leftover up to each track's growth limit.
4. **Expand Flexible Tracks** (§11.7) — distribute remaining space among `fr` tracks per their flex factor using the spec's *used flex fraction* algorithm (NOT a naive `flex / sumFlex` proportional split).
5. **Expand Stretched `auto` Tracks** (§11.8) — when `align-content: stretch` / `justify-content: stretch`, grow auto tracks to fill any remaining space.

Each pass interacts with the others through item-span boundaries, growth limits, and the distribution rules. Track types (`auto`, `fr`, `<length>`, `minmax`, `fit-content`, `repeat`) each have their own contribution logic.

**Strategy:** progressive enhancement across small cycles. **Cycle 0** lands CSS-infrastructure groundwork (= property types + source-gen + resolver + ComputedSlot side-table + shorthand expansion + property-coverage tests). **Cycle 1** ships the simplest viable layouter slice (= explicit pixel tracks + integer-line + sparse auto-placement) that proves dispatch + emission end-to-end. Each subsequent cycle adds one track type or one placement feature.

---

## 2. Existing scaffolding (= what's already there) — verified post-PR-#88 P2 #8

- `BoxKind.GridContainer` + `BoxKind.InlineGridContainer` — present in `src/NetPdf.Layout/Boxes/BoxKind.cs:99-102, 132`.
- `DisplayMapper` already maps `display: grid` → `GridContainer` (line 110-111) + `display: inline-grid` → `InlineGridContainer` (line 134-135).
- `GridContinuation` record — present in `src/NetPdf.Paginate/LayoutContinuation.cs:107-109` with `RowIndex` + opaque `TrackSizingCache`. The `TrackSizingCache` shape will be defined in cycle 5 (multi-page grid).
- No `GridLayouter` class yet.
- No grid-related CSS properties in `src/NetPdf.Css/properties.json`.
- **Current `BlockLayouter` behavior for `BoxKind.GridContainer`** (verified at `BlockLayouter.cs:4198-4203` — `IsBlockFlowContainerOwnedByBlockLayouter` returns false for GridContainer):
  - The outer block-flow loop emits a placeholder `BoxFragment` for the grid container at its declared border-box size.
  - The recursive walk **does NOT descend into grid items** (= the predicate gates them out as "non-flow, owned by GridLayouter").
  - **Net effect today: grid items are silently dropped** — the container renders empty. NOT "mis-emitted as block siblings" as the v1 design doc erroneously claimed.

---

## 3. Cycle plan (revised)

### Cycle 0 — CSS infrastructure for grid properties (Task 17 prerequisite, ~2 d)

**Per PR-#88 review P1 #1**: new property types like `TrackList` / `GridLine` can't be wired without source-gen + resolver groundwork. This cycle does NO layouter work — it adds the CSS pipeline support that cycles 1+ depend on.

**Scope:**
- Add `PropertyType` enum entries:
  - `GridTemplateList` — for `grid-template-rows` / `grid-template-columns` (= parsed AST, not yet expanded).
  - `GridLine` — for `grid-row-start` / `grid-row-end` / `grid-column-start` / `grid-column-end`.
- Update `CssPropertyGenerator` source-gen allowlist to recognize the new types + emit accessor methods on `ComputedStyle`.
- Add resolver dispatch (`PropertyResolver.cs` or equivalent) for the new types — parses the CSS value string into the AST.
- Add `ComputedSlot` payload storage:
  - For small/inline cases (single GridLine), use existing `ComputedSlot` tags (Integer for line numbers).
  - For complex parsed ASTs (TrackList), use the side-table pattern (= `SideTableIndex` tag → entry in a typed dictionary on `ComputedStyle`). Mirrors the existing pattern for `FlexBasis`-style complex values.
- Add shorthand expansion in the cascade:
  - `grid-row: 2` → `grid-row-start: 2; grid-row-end: auto;` (= single-line shorthand)
  - `grid-row: 2 / 4` → `grid-row-start: 2; grid-row-end: 4;` (= explicit-pair shorthand)
  - `grid-column: 2 / span 3` → `grid-column-start: 2; grid-column-end: span 3;`
  - `grid-area: <ident>` and 4-value `grid-area` → expanded similarly (deferred to cycle 7 since named areas land then).
- Property-coverage tests:
  - Each new property has a `*Tests.cs` round-trip (declared CSS → cascade → ComputedStyle → typed reader returns expected AST).
  - Shorthand expansion tests verify each longhand gets the right value.
  - Default-value tests (= `grid-row` unset → `grid-row-start: auto`, `grid-row-end: auto`).

**Done when:** all 6 new grid properties (`grid-template-rows`, `grid-template-columns`, `grid-row-start/end`, `grid-column-start/end`) round-trip through the cascade with their typed accessor returning the expected AST. NO layout work yet.

### Cycle 1 — Hello World: explicit pixel tracks + integer placement + minimal auto-placement (Task 17 start, ~3-4 d)

**Per PR-#88 review P1 #2**: grid items DEFAULT to auto placement (`grid-row-start: auto`). A Hello World without minimal auto-placement would only handle items where the author explicitly declared every grid-row/grid-column — exceedingly rare. Cycle 1 ships **source-order sparse auto-placement** (= the spec's default behavior).

**Scope:**
- `TrackList` parser accepts only `<length>` tracks (no `fr`, `auto`, `minmax`, `fit-content`, `repeat`, or named lines).
- `<grid-line>` parser accepts only `<integer>` + `auto` (no `span`, no named lines).
- New `GridLayouter` class:
  - `ConfigureEmission(contentInlineOffset, contentBlockOffset, contentInlineSize, contentBlockSize, allowPagination: false)` — mirrors `FlexLayouter.ConfigureEmission`.
  - Track-position computation: cumulative sum of declared `<length>` tracks in CSS order. Track count = explicit grid size.
  - **Item placement algorithm (sparse, per CSS Grid L1 §8.5)**:
    1. Initialize a 2-D occupancy grid (= row × column boolean grid; size = explicit-row-count × explicit-column-count).
    2. Walk items in source order (= DOM order):
       - If item has explicit `grid-row-start: <int>` AND `grid-column-start: <int>`: place at the declared cell. If occupied, behavior follows §8.5 (= place anyway; cells can overlap visually but the painter renders each fragment independently).
       - Else: find the next free cell in row-major order (= sparse) + claim it. Single-cell items only in cycle 1 (no `span`).
    3. Items requiring auto-row placement (= row=auto + column declared) walk down the declared column until finding free cell. Vice versa for auto-column.
  - Each item's fragment: at the resolved cell's top-left, sized to the cell's `width × height` (= track-derived). No item-level alignment (`justify-self` / `align-self`) — items fill the cell. Cell-internal alignment lands in a later cycle.
- `BlockLayouter` dispatch wiring at 3 sites (outer ~line 2280, recursive ~line 3460, forced-overflow ~line 1990) — mirrors the FlexLayouter cycle-4a `DispatchFlexInner` pattern. New private helper `DispatchGridInner`.
- New `GridGeometryHelper.ComputeContentGeometry(...)` — mirrors `FlexGeometryHelper` (cycle-4d pattern).
- Hello World tests:
  - Direct-construction: 2×2 grid with `grid-template-rows: 100px 200px`, `grid-template-columns: 50px 150px`, 4 items declared at unique `(grid-row, grid-column)` → assert 4 fragments at expected (offset, size).
  - Direct-construction: 2×2 grid + 4 items with NO explicit placement → assert sparse auto-placement fills (1,1), (1,2), (2,1), (2,2) in source order.
  - Production-pipeline: HTML with explicit grid tracks + items → assert correct emission.
- Deferrals documented up-front:
  - All non-pixel track types (cycle 2+).
  - `span`, named lines, named areas (cycles 5-7).
  - `grid-auto-flow: dense` (cycle 7).
  - Cross-cell alignment (`justify-self` / `align-self`) — separate Task.
  - Multi-page grid (cycle 5).

**Done when:** a 2×2 grid renders 4 items at expected pixel positions through both unit + production paths, with both explicit-placement AND source-order auto-placement covered.

### Cycle 2 — `fr` units (Task 17, ~2 d)

**Per PR-#88 review P1 #3**: the v1 design's `flex / sumFlex` proportional formula is *wrong* for fractional flex factors + negative leftover. Cycle 2 implements the spec-correct **used flex fraction** algorithm (CSS Grid L1 §11.7.1).

**Scope:**
- `TrackList` parser accepts `<flex>` values (= `1fr`, `0.25fr`, `2fr`, etc.).
- Track-sizing Step 4 (Expand Flexible Tracks) per §11.7 implements the spec **Find the Size of an fr** algorithm:
  ```
  Inputs: leftoverSpace (= containerSize - sumOfNonFlexTrackBaseSizes),
          fr tracks with their flex factors.
  1. Let flexFactorSum = Σ over fr tracks of max(flex, 1).
     // Per §11.7.1: fr factors < 1 are treated as 1 for sumFlex, but
     // contribute their fractional value to per-track distribution.
  2. Let hypotheticalFrSize = leftoverSpace / flexFactorSum.
  3. For each fr track with flex < 1 AND base size > hypotheticalFrSize × flex:
     remove it from the calculation (= treat as fixed at its base size);
     subtract its base from leftoverSpace; iterate.
  4. Used flex fraction = leftoverSpace / Σ flex over remaining fr tracks
     (with each fr factor floored at 1 in the divisor).
  5. Each fr track's final size = max(baseSize, usedFlexFraction × flex).
  ```
- Edge cases the spec handles + we test:
  - `flex < 1` (= `0.25fr`) → the per-track contribution is fractional even though sumFlex floors at 1.
  - Negative leftover space (= fixed tracks already exceed container) → fr tracks pin at base size; container overflows.
  - `minmax(min, fr)` interaction: an fr inside minmax respects the min constraint (full impl in cycle 4).
- Tests:
  - `grid-template-columns: 1fr 1fr` in 600px container → 300/300.
  - `grid-template-columns: 200px 1fr 2fr` in 600px → 200/133.33/266.67.
  - `grid-template-columns: 0.25fr 0.25fr` in 100px → 50/50 (= sumFlex floored at 1 + 1, each track = 100 × 0.25/(0.5+0.5) → spec produces 25/25 with overflow; verify exact spec output).
  - `grid-template-columns: 300px 1fr` in 200px → 300/0 (= fr can't be negative; container overflows).
  - 4+ tests covering edge cases.

### Cycle 3 — `auto` / `min-content` / `max-content` intrinsic sizing (Task 17, ~3 d)

**Scope:**
- `TrackList` parser accepts `auto`, `min-content`, `max-content`.
- Implement track-sizing algorithm Steps 1-3 (Initialize + Resolve Intrinsic + Maximize) per §11.5-11.6.
- Requires content measurement. Per PR-#88 review + the L19 underlying deferral, **cycle 3 ships with documented approximation**:
  - For items with explicit `width`/`height` on the auto-track-spanning axis: contribution = declared length. (= "specified size suggestion" per CSS Sizing §5.5.)
  - For items without explicit dimensions: contribution = 0. (= matches the flex `min-width: auto` deferral; full intrinsic sizing arrives with L19.)
- `min-content` + `max-content` keyword: under the cycle-3 approximation both resolve to the same value as `auto`. Document as known-gap until L19 lands.
- Tests pin the cycle-3 approximation behavior + are marked with a documented "flip when L19 ships" note.

### Cycle 4 — `minmax()` + `fit-content()` + `repeat(<integer>, ...)` (Task 17, ~2 d)

**Per PR-#88 review P2 #5**: `fit-content(limit)` semantics per CSS Grid L1 §7.2.2 = `max(auto-minimum, min(limit, max-content))`. NOT the simpler `minmax(auto, max-content)` clamped form.

**Scope:**
- CSS function parsing for `minmax(min, max)`, `fit-content(limit)`, `repeat(<integer>, <track-list>)`.
- `repeat()` supports integer counts only in cycle 4 (no `auto-fill` / `auto-fit` — deferred to cycle 7).
- `minmax(min, max)` integration with track-sizing:
  - Base size from `min` argument (LengthPx → that value; auto → 0 with intrinsic-resize per cycle 3).
  - Growth limit from `max` argument.
  - Step 3 (Maximize) respects the growth limit.
- `fit-content(limit)` exact formula per §7.2.2:
  ```
  Effective size = max(auto-minimum, min(limit, max-content))
  Where:
    auto-minimum = 0 (cycle 3 approximation; L19 ships true content-size suggestion)
    max-content  = item's max-content contribution
    limit        = the LengthPx limit argument
  ```
- Tests:
  - `minmax(100px, 200px)` with content needing 150 → 150.
  - `minmax(100px, 200px)` with content needing 300 → 200 (= clamped).
  - `fit-content(100px)` with min-content 150 (= exceeds limit) → 150 (auto-minimum dominates).
  - `fit-content(200px)` with max-content 80 (= below limit) → 80.
  - `repeat(3, 100px)` → 3 tracks of 100.
  - `repeat(2, 100px 1fr)` → 4 tracks: 100/1fr/100/1fr.

### Cycle 5 — multi-page grid pagination (Task 18 start, ~3 d)

**Per PR-#88 review P2 #7**: grid items spanning multiple rows need explicit pagination semantics. The cycle-5 contract:

**Scope:**
- `GridLayouter` paginates **row-by-row** (= each row is the unit; intra-row item splitting is post-v1).
- **Row-spanning item behavior** (concrete decision, NOT deferred):
  - **Spanning items are ATOMIC to their row span.** If an item spans rows N..M and ANY of those rows would land on a later page, the entire item defers to the resume page.
  - The first page emits rows 1..K where K is the max index such that no item spanning rows ≤ K has any row > K AND row K fits the page budget. (= "no item straddles the page break").
  - If a single row+spanning-items unit is taller than the fragmentainer, force-overflow per CSS Fragmentation L3 §4.4 progress rule (= emit anyway + diagnostic).
- `GridContinuation` cache extended:
  - `RowIndex` (existing) — next row index to emit on resume.
  - **New: `TrackSizingCache`** — pre-resolved track sizes + column positions so the resume page skips the (expensive) track-sizing pass. Shape: `record TrackSizingCache(ImmutableArray<double> RowBaseSizes, ImmutableArray<double> ColumnBaseSizes)`.
  - **New: `PlacementCache`** — pre-resolved (item → cell) mapping so resume doesn't re-run sparse auto-placement (= would yield different placement if items were partially emitted on the prior page).
- `BlockLayouter` dispatch: paginatable-grid extent clamp + `allowPagination: true` flip — mirrors flex cycle-4b pattern.
- Multi-page production tests:
  - 3×2 grid where rows 1+2 fit on page 1 + row 3 defers.
  - 2×2 grid where item spans rows 1-2 + container budget = row 1 height only → entire item defers (no orphan top half on page 1).
  - Round-trip: feed page-1 continuation back → page 2 emits remaining rows → all items appear exactly once.

### Cycle 6 — `<grid-line>: span N` + auto-placement spans (Task 18, ~2 d)

**Scope:**
- `<grid-line>` parser accepts `span <integer>`.
- Item placement algorithm handles spanning items:
  - Explicit-row span: `grid-row: 2 / span 3` → item spans rows [2, 5).
  - Auto-row span: `grid-row: span 2` → item gets placed in next free 2-row run.
- Implicit tracks: when items extend past the explicit grid, add tracks via `grid-auto-rows` / `grid-auto-columns`.
- `grid-auto-flow: row` (default) / `column` — the row-major vs column-major fill order for auto-placed items.
- Tests covering span + auto-flow interactions.

### Cycle 7 — `grid-template-areas` + named lines + auto-fill/auto-fit + dense (Task 18 finish, ~3 d)

**Scope:**
- `grid-template-areas` string-grammar parser (§7.3) — multi-string syntax `"head head" "main side" "foot foot"`.
- Named line + named area placement (`grid-row-start: <area-name>`, `grid-area: <ident>`).
- `repeat(auto-fill, ...)` + `repeat(auto-fit, ...)` (= count derived from container size at layout time).
- `grid-auto-flow: dense` — backtracking placement that fills holes left by sparse mode.
- Tests for each feature.

### Out-of-scope follow-ons (tracked in `docs/deferrals.md`)

- **Subgrid** (`subgrid` keyword) — CSS Grid L2 only; post-v1 per phase doc.
- **Masonry layout** (`grid-template-rows: masonry`) — CSS Grid L3 draft; out-of-scope for v1.
- `justify-items` / `align-items` / `justify-self` / `align-self` on grid items — landing in a separate Task that shares the `<self-position>` decoder with FlexLayouter (= the L9 hardening F#3 shared-extension refactor).
- Baseline alignment in grid cells — blocked on L18 baseline-position content measurement.
- Full intrinsic sizing for `auto` tracks — blocked on L19 content measurement (cycle 3 ships approximation only).
- `grid` shorthand parser (= simultaneous template-rows + template-columns + auto-flow) — Task 19/20 housekeeping after cycle 7.

---

## 4. Data model (revised per PR-#88 review P2 #4 + P2 #6)

**Per PR-#88 review P2 #4**: a record struct can't be self-referential in C# (= compile error). The track AST uses a discriminated-union shape with no inner-struct recursion. **Per PR-#88 review P2 #6**: TrackList stays as an immutable parsed AST in `ComputedStyle`; expansion (= resolving `repeat()` counts, named-line lookup, auto-fill/auto-fit container-derived count) happens at LAYOUT TIME inside `GridLayouter`, NOT eagerly during cascade.

```csharp
// In src/NetPdf.Css/ComputedValues/Grid.cs (new file).

/// <summary>Per CSS Grid L1 §7.2 — the kind of one entry in a
/// track list. Determines which payload fields are read.</summary>
internal enum GridTrackKind : byte
{
    LengthPx,          // <length>; value in TrackEntry.LengthPx
    Fr,                // <flex>; value in TrackEntry.FrValue
    Auto,              // auto / min-content / max-content
                       // (cycle 3 approximates all three as 0)
    MinMax,            // minmax(min, max); MinSubKind / MinValue
                       // + MaxSubKind / MaxValue payload
    FitContent,        // fit-content(limit); LengthPx = limit, no recursion
}

/// <summary>One entry in a parsed track list. Flat (= no recursion)
/// for trivial codegen + zero-allocation iteration. The MinMax case
/// stores its sub-args inline rather than referencing other TrackEntry
/// instances — keeps the type a record struct.</summary>
internal readonly record struct TrackEntry
{
    public GridTrackKind Kind { get; init; }
    public double LengthPx { get; init; }      // For LengthPx, FitContent (the limit).
    public double FrValue { get; init; }       // For Fr.
    public GridTrackKind MinSubKind { get; init; }  // For MinMax: the min arg's kind
    public double MinSubLengthPx { get; init; }     //   (only LengthPx + Auto allowed
    public double MinSubFr { get; init; }           //    per §7.2.4 — Fr in `min` is invalid)
    public GridTrackKind MaxSubKind { get; init; }  // For MinMax: the max arg's kind
    public double MaxSubLengthPx { get; init; }
    public double MaxSubFr { get; init; }
}

/// <summary>One <c>repeat()</c> group in a parsed track list. May
/// expand to multiple TrackEntries at layout time (= integer count
/// expands at parse time; auto-fill/auto-fit defer to layout-time
/// container-size knowledge).</summary>
internal sealed record TrackRepeat(
    int Count,              // 0 = auto-fill, -1 = auto-fit, positive = explicit count.
    ImmutableArray<TrackEntry> Pattern);

/// <summary>The parsed AST for a <c>grid-template-rows</c> /
/// <c>-columns</c> declaration. Stored on ComputedStyle via the
/// SideTableIndex pattern (= heap-only; ComputedSlot carries the
/// table index). Layout-time expansion via
/// <c>TrackList.Expand(containerSize, …)</c>.</summary>
internal sealed record TrackList(
    // Mixed inline-entries + repeat-groups, preserving CSS source order
    // so layout-time expansion produces the spec-correct sequence.
    ImmutableArray<TrackListItem> Items);

internal abstract record TrackListItem;
internal sealed record TrackListEntry(TrackEntry Entry) : TrackListItem;
internal sealed record TrackListRepeat(TrackRepeat Repeat) : TrackListItem;
internal sealed record TrackListNamedLine(string Name) : TrackListItem;
// Named lines (= [name] in CSS) are stored inline between entries;
// cycle 7 reads them for grid-row-start: <name> resolution.

/// <summary>A grid-line value (= grid-row-start / grid-row-end /
/// grid-column-start / grid-column-end). Trivially flat — no recursion.</summary>
internal readonly record struct GridLineValue
{
    public GridLineKind Kind { get; init; }     // Auto / LineNumber / Span / NamedLine
    public int LineNumber { get; init; }        // For LineNumber + Span (= span N)
    public string? NamedLine { get; init; }     // For NamedLine (cycle 7)
}

internal enum GridLineKind : byte
{
    Auto,
    LineNumber,
    Span,
    NamedLine,
}
```

**ComputedStyle storage:**
- `GridLineValue` fits in `ComputedSlot` directly (= 1 byte kind + 4 bytes int + nullable string). For cycle 1-6 the NamedLine path is nullable so the struct is small.
- `TrackList` does NOT fit in `ComputedSlot` (= variable-size payload). Uses the side-table pattern: `ComputedSlot.SideTableIndex` tag → entry in a typed dictionary keyed by table index, value is `TrackList`. Pattern is established by `FlexBasis`-content / `font-family`-list / similar complex-AST properties.

**Resolution timing:**
- **Parse time** (cascade): full AST built. `repeat(<integer>, ...)` literal-count expansion happens at parse time (= constant-folded since it doesn't depend on layout context).
- **Layout time** (GridLayouter.AttemptLayout): final `ImmutableArray<TrackEntry>` resolution. `auto-fill` / `auto-fit` expansion happens here (= needs container size). Named-line lookup against the parsed AST happens here (= cycle 7).

---

## 5. Test strategy

Mirrors the flex test layout pattern:

- **Property-coverage tests** (cycle 0 ships these first):
  - Round-trip declarations through cascade → ComputedStyle → typed reader.
  - Shorthand expansion verifies each longhand.
- **Direct-construction layouter tests** (`GridLayouterTests.cs`):
  - Hand-built `Box` trees, exact fragment offset/size pinning.
  - Each cycle adds 6-10 tests.
- **Production-pipeline tests** (`GridLayouterProductionTests.cs`):
  - HTML → BlockLayouter → GridLayouter end-to-end.
  - Verify dispatch + cycle-feature integration.
- **W3C CSS conformance tests** (in `tests/NetPdf.W3cConformance/`):
  - Pinned subset; expands per cycle. Track pass-rate per phase-doc §"GridLayouter | W3C subset".

Total expected: ~100-140 grid-specific tests across all cycles.

---

## 6. Risks + dependencies

### Architectural

- **Intrinsic sizing dependency (cycle 3)** — `auto` / `min-content` / `max-content` tracks need content measurement. Cycle 3 ships approximation per the flex deferral pattern.
- **Two-pass track sizing performance** — phase-doc gate is 3-page invoice ≤ 200 ms p50. Profile early in cycle 2.
- **GridContinuation cache shape** — `TrackSizingCache` + new `PlacementCache` need to be value-type or pooled records to avoid allocation churn across page boundaries.

### CSS parsing

- The CSS Grid grammar is complex: `<track-list>` has nested `repeat()`, `minmax()`, `[name]` lines interleaved with entries. Cycle 0 lands the parser groundwork; subsequent cycles add specific function/keyword forms.
- `grid` shorthand is post-cycle-7 housekeeping.

### Clean-room policy (per `docs/clean-room-policy.md` + `phase-3-layout-and-pagination.md` line 111)

- Read Servo's `layout_2020/grid` and Taffy's grid implementation **for understanding** the algorithm interpretation only — NOT for transliteration.
- Leave a one-line note in `GridLayouter.cs` for each external implementation consulted.
- Implementation derives from CSS Grid L1 §11 spec text.

### Cross-layouter coordination

- Item alignment (`justify-items`, `align-items`, etc.) shares the `<self-position>` decoder with FlexLayouter (= L9 hardening F#3 shared-extension refactor — already shipped).
- `<baseline-position>` alignment in grid cells: same deferral as flex L18.

---

## 7. Implementation order summary (revised)

| Cycle | Title | CSS slice | Algorithm | Tests | Est. |
|---|---|---|---|---|---|
| 0 | CSS infrastructure | new types in source-gen + resolver | n/a (parse pipeline) | property coverage | 2 d |
| 1 | Hello World | explicit `<length>` tracks + integer + sparse auto-place | sum tracks; row-major scan | direct + production | 3-4 d |
| 2 | `fr` units | `<flex>` tracks | Step 4 spec "used flex fraction" | + 0.25fr, overflow, minmax | 2 d |
| 3 | `auto` tracks | `auto` / `min-content` / `max-content` | Steps 1-3 (intrinsic, L19-approx) | + known-gap tests | 3 d |
| 4 | `minmax()`/`fit-content()`/`repeat(int)` | CSS functions | growth-limit + fit-content formula | + parser tests | 2 d |
| 5 | multi-page grid | continuation chain | row-boundary pagination + atomic-span | + multi-page production | 3 d |
| 6 | `span` + auto-placement spans | `<grid-line>: span N` + auto-flow | placement w/ spans | + sparse-flow tests | 2 d |
| 7 | named areas + auto-fill/auto-fit + dense | `grid-template-areas`, `repeat(auto-...)`, `dense` | dense packing + container-derived count | + named-area tests | 3 d |

**Estimated total:** ~20 days across 8 cycles. Slightly over the phase-plan budget (Task 17: 8 d + Task 18: 5 d = 13 d) but more honest given the CSS-infrastructure cycle 0 wasn't accounted for in the original budget.

---

## 8. Done criteria for the Task 17 + 18 series

- All 8 cycles shipped via separate PRs (each merged after Roland review per the standing workflow).
- W3C grid spec sample tests pass (subset pinned in `tests/NetPdf.W3cConformance/`).
- 3-page invoice render still hits ≤ 200 ms p50 (= performance gate per CLAUDE.md cross-cutting rule #8).
- AOT/JIT byte-parity preserved across all PRs.
- All deferrals from the cycle plan + the "out-of-scope follow-ons" section above tracked in `docs/deferrals.md`.

---

## 9. Discoverability (per PR-#88 review P3 #9)

This design doc is linked from:

- `docs/phases/phase-3-layout-and-pagination.md` — Task 17 row in the work breakdown table.
- `docs/deferrals.md` — a new "GridLayouter (Task 17 + 18)" entry pointing here for the active design.
- `PROGRESS.md` — current-phase pointer mentions Task 17 design in progress.
- `CLAUDE.md` — already references the phase docs via the "Know what to build next" entry; no additional link needed (= the phase doc IS the discovery path).

The pre-cycle-0 commit that lands this design includes the PROGRESS.md / phase-doc / deferrals.md link additions.

---

## 10. Open design questions (refined post-PR-#88 review)

1. ~~**Cycle granularity** — 7 vs more sub-cycles~~. **Resolved**: 8 cycles (added cycle 0 for CSS infrastructure). Each cycle is small enough that it ships in 2-4 days; coarser than flex L1-L17 because grid's per-feature surface area is much larger.
2. ~~**L19 dependency for cycle 3**~~. **Resolved**: ship cycle 3 with documented approximation (matching the flex `min-width: auto` deferral pattern). Full closure waits for L19.
3. ~~**Grid L1 vs L2**~~. **Resolved**: target = CSS Grid L1. L2 adds subgrid (post-v1). Masonry is CSS Grid L3 (= explicitly out-of-scope; not part of L2 contrary to v1 design doc's claim).
4. ~~**`grid` shorthand**~~. **Resolved**: post-cycle-7 housekeeping in Task 19/20.
5. **NEW: row-spanning item pagination semantics (= cycle 5 design call)** — confirmed atomic-to-row-span per §10. If Roland prefers split-spanning-items semantics, redesign cycle 5 to accept a continuation cache per row + per spanning-item.
6. **NEW: TrackSizingCache + PlacementCache concrete shapes** — design open until cycle 5; sketch in §4 above pending validation that the cache survives `GridContinuation` chain serialization without bloating LayoutContinuation allocations.

---

## 11. Changelog

- **v1 (initial)** — first design pass. 7 cycles, all CSS work bundled into the layouter cycles.
- **v2 (post-PR-#88 review)** — addresses all 10 review findings:
  - Added cycle 0 (CSS infrastructure groundwork) per P1 #1.
  - Cycle 1 now includes minimal source-order sparse auto-placement per P1 #2.
  - Cycle 2 fr formula replaced with spec-shaped "used flex fraction" per P1 #3.
  - Cycle 4 fit-content() formula corrected per P2 #5.
  - Data model revised: flat TrackEntry struct (no recursion) per P2 #4; layout-time expansion preserves AST per P2 #6.
  - Cycle 5 spans semantics + cache shape made concrete per P2 #7.
  - Scaffolding inventory corrected (= silent item drop, NOT mis-emit) per P2 #8.
  - Discoverability links added per P3 #9.
  - L1/L2/Masonry version target clarified per P3 #10.
  - Open questions list updated (resolved → marked resolved; new ones added).
