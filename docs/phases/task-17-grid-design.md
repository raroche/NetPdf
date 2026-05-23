# Task 17 — `GridLayouter` design

**Status:** Design draft (no code yet). Pending Roland review before cycle 1 starts.

**Spec:** CSS Grid Layout L1 ([https://www.w3.org/TR/css-grid-1/](https://www.w3.org/TR/css-grid-1/)) — track sizing in §11, item placement in §8, named areas in §7.3.

**Phase-plan scope:**
- Task 17 (8 d): track-sizing algorithm
- Task 18 (5 d): placement + dense packing + areas

This doc covers both Task 17 and Task 18 since they share the data model + box-builder + layouter scaffolding. The cycle plan splits the work across both tasks.

---

## 1. Why this is the hardest layouter

CSS Grid's track-sizing algorithm (§11.4) is widely regarded as the most complex layout algorithm in CSS. It is a multi-pass iterative algorithm:

1. **Initialize Track Sizes** — set each track's base + growth limit from its sizing function.
2. **Resolve Intrinsic Track Sizes** — distribute item min/max-content contributions across the tracks the item spans.
3. **Maximize Tracks** — when a definite container size is given, distribute leftover space up to the growth limits.
4. **Expand Flexible Tracks** — distribute remaining space among `fr` tracks per their flex factor.
5. **Expand Stretched `auto` Tracks** — when `align-content: stretch` / `justify-content: stretch`, grow auto tracks to fill.

Each pass interacts with the others through item-span boundaries, growth limits, and the distribution rules. Track types (`auto`, `fr`, `<length>`, `minmax`, `fit-content`, `repeat`) each have their own contribution logic.

**Strategy:** progressive enhancement across small cycles. Cycle 1 ships the simplest possible spec slice (= explicit pixel tracks + explicit integer line placement) that proves the dispatch + emission shape works end-to-end. Each subsequent cycle adds one track type or one placement feature.

---

## 2. Existing scaffolding (= what's already there)

- `BoxKind.GridContainer` + `BoxKind.InlineGridContainer` — present in `src/NetPdf.Layout/Boxes/BoxKind.cs`.
- `DisplayMapper` already maps `display: grid` → `GridContainer` (line 110-111) + `display: inline-grid` → `InlineGridContainer` (line 134-135).
- `GridContinuation` record — present in `src/NetPdf.Paginate/LayoutContinuation.cs` (line 107-109) with `RowIndex` + opaque `TrackSizingCache`. Ready for cycle-N multi-page-grid support.
- No `GridLayouter` class yet.
- No grid-related CSS properties in `src/NetPdf.Css/properties.json`.
- `BlockLayouter` doesn't dispatch GridContainer anywhere (it likely falls through to block-flow recursive walk, which mis-emits items as block siblings).

---

## 3. Cycle plan

Each cycle is a self-contained PR that ships behavior + tests + docs. Cycles are ordered so the Hello World ships fast (= proves the dispatch + emission integration end-to-end) + later cycles add CSS features incrementally without re-architecting the layouter.

### Cycle 1 — Hello World: explicit pixel tracks + explicit integer placement (Task 17 start, ~2-3 d)

**Scope:**
- New CSS properties (added to `properties.json` + parser support):
  - `grid-template-rows` (type: `TrackList`, default: `none`)
  - `grid-template-columns` (type: `TrackList`, default: `none`)
  - `grid-row` (= shorthand for `grid-row-start` + `grid-row-end`) — integer values only
  - `grid-column` (= shorthand for `grid-column-start` + `grid-column-end`) — integer values only
  - `grid-row-start`, `grid-row-end`, `grid-column-start`, `grid-column-end` (longhands)
- `TrackList` typed value supports only `<length>` track entries (pixel values). No `fr`, no `auto`, no `minmax`, no `repeat`, no named lines.
- `<grid-line>` typed value supports only `<integer>` (line numbers). No `span`, no `auto`, no `<custom-ident>`.
- New `GridLayouter` class:
  - `ConfigureEmission(contentInlineOffset, contentBlockOffset, contentInlineSize, contentBlockSize, allowPagination: false)` — mirrors `FlexLayouter`'s shape.
  - `AttemptLayout(...)` — emits `BoxFragment` for each grid item at its resolved (row, column) cell position.
  - Track positions: cumulative sum of declared `grid-template-rows` / `grid-template-columns` values, in CSS order.
  - Each item's cell rect: `[row_start_line, row_end_line) × [col_start_line, col_end_line)`. Item paints at the cell's top-left + sized to the cell's content area (post-borders/padding on container).
- BlockLayouter dispatch wiring at all 3 sites (outer ~line 2280, recursive ~line 3460, forced-overflow ~line 1990) — mirrors the FlexLayouter cycle-4a `DispatchFlexInner` pattern.
- New `GridGeometryHelper.ComputeContentGeometry(...)` — mirrors `FlexGeometryHelper` (cycle-4d pattern).
- Hello World tests:
  - Direct-construction: 2×2 grid with `grid-template-rows: 100px 200px`, `grid-template-columns: 50px 150px`, 4 items each at unique `(grid-row, grid-column)` → assert 4 fragments at expected (offset, size).
  - Production-pipeline: HTML with explicit grid tracks + items → assert correct emission.
- Deferrals documented up-front:
  - All non-pixel track types (cycle 2+).
  - Auto-placement (cycle 5).
  - `span`, named lines, named areas (cycles 4-5).
  - Multi-page grid (cycle 6).

**Done when:** a 2×2 grid with explicit pixel tracks + explicit integer placement renders 4 items at expected pixel positions through both the unit test path AND the production HTML pipeline.

### Cycle 2 — `fr` units + Step 4 of §11.4 (Task 17, ~1.5 d)

**Scope:**
- `TrackList` parser accepts `<flex>` values (= `1fr`, `2fr`, etc.).
- Implement track-sizing algorithm Step 4 (Expand Flexible Tracks) per §11.7:
  - `flexFactorSum = Σ track.flex`
  - `leftoverSpace = containerSize - Σ fixed-track-sizes`
  - For each fr track: `track.size = leftoverSpace × (track.flex / max(flexFactorSum, 1))`
  - (Steps 1-3 trivial in this cycle because there are no intrinsic-sized tracks.)
- Tests: `grid-template-columns: 1fr 1fr` in a 600px container → each track 300px.
- Tests: `grid-template-columns: 200px 1fr 2fr` in a 600px container → tracks 200/133.33/266.67.
- Tests: fractional `flex < 1` (= L8 F#2 hardening pattern from flex).

### Cycle 3 — `auto` tracks + intrinsic sizing (Task 17, ~3 d)

**Scope:**
- `TrackList` parser accepts `auto`, `min-content`, `max-content`.
- Implement track-sizing algorithm Steps 1-3 (Initialize + Resolve Intrinsic + Maximize) per §11.5-11.6.
- Requires content measurement (= the underlying L19 deferral). For cycle 3 MVP:
  - For items with explicit `width`/`height` on the auto-track-spanning axis: contribution = declared length.
  - For items without explicit dimensions: contribution = 0 (= the deferral-approximation per CSS Sizing §5; full intrinsic sizing arrives with L19).
- Tests: `auto` tracks contained by declared item widths; `min-content` + `max-content` keyword equivalence to `auto` for the approximation.
- Document the cycle-3 limitation as a known-gap test that flips when L19 ships content measurement.

### Cycle 4 — `minmax()` + `fit-content()` + `repeat()` (Task 17, ~2 d)

**Scope:**
- CSS function parsing for `minmax(min, max)`, `fit-content(limit)`, `repeat(count, track-list)`.
- `repeat()` supports integer counts only in cycle 4 (no `auto-fill` / `auto-fit` — deferred to cycle 7 since those depend on the container size).
- `minmax()` integration with the track-sizing algorithm (Step 2's growth-limit handling).
- `fit-content(limit)` = `minmax(auto, max-content)` clamped to `limit`.
- Tests for each function form.

### Cycle 5 — auto-placement + `span` (Task 18 start, ~2 d)

**Scope:**
- `<grid-line>` parser accepts `span <integer>` + `auto`.
- Item placement algorithm (§8): walk items, place each at the next available cell respecting their span counts. Sparse mode only (= `grid-auto-flow: row` default; dense packing is cycle 7).
- `grid-auto-rows` + `grid-auto-columns` (implicit tracks for items placed beyond the explicit grid).
- `grid-auto-flow: row` (default) / `column`.
- Tests: items with only `grid-column: span 2` (= auto-placed rows) + items with `grid-row: 1 / span 2`.

### Cycle 6 — multi-page grid pagination (Task 18 follow-on, ~2 d)

**Scope:**
- `GridLayouter` paginates row-by-row (= each row is the unit; intra-row item splitting is post-v1).
- `BlockLayouter` dispatch: paginatable-grid extent clamp + `allowPagination: true` flip — mirrors flex cycle-4b pattern.
- `GridContinuation.RowIndex` resume contract.
- `TrackSizingCache` populated on PageComplete so the resume page skips the (expensive) track-sizing pass.
- Multi-page production tests.

### Cycle 7 — `grid-template-areas` + named lines + auto-fill/auto-fit (Task 18 finish, ~2 d)

**Scope:**
- `grid-template-areas` string-grammar parser (§7.3).
- Named line + named area placement (`grid-row-start: <area-name>`).
- `repeat(auto-fill, ...)` + `repeat(auto-fit, ...)` (= count derived from container size).
- `grid-auto-flow: dense` (= dense packing).
- Tests for each.

### Out-of-scope follow-ons (post-cycle-7, tracked in deferrals.md)

- Subgrid (`subgrid` keyword) — post-v1 per phase doc.
- Masonry layout (CSS Grid L3) — post-v1.
- `align-items` / `justify-items` / `align-self` / `justify-self` on grid items — landing in a separate Task that shares the alignment helpers with FlexLayouter (= the `<self-position>` decoder from L9's shared-extension refactor).
- Baseline alignment in grid cells — blocked on L18 baseline-position content measurement.

---

## 4. Data model

```csharp
// New types in NetPdf.Css.ComputedValues (or similar).

internal enum GridTrackKind : byte
{
    LengthPx,        // <length>
    Fr,              // <flex>
    Auto,            // auto / min-content / max-content
    MinMax,          // minmax(min, max)
    FitContent,      // fit-content(limit)
}

internal readonly record struct GridTrack(
    GridTrackKind Kind,
    double Value,             // px for LengthPx, fr count for Fr
    GridTrack? MinTrack,      // for MinMax
    GridTrack? MaxTrack,      // for MinMax
    double FitLimit);         // for FitContent

internal sealed record TrackList(
    ImmutableArray<GridTrack> Tracks);  // expanded post-repeat()

// Item placement.
internal readonly record struct GridLine(
    int LineNumber,           // negative = end-relative; 0 = auto
    int Span);                // 0 = explicit line, ≥1 = span
```

The full `TrackList` (= post-`repeat()` expansion) is computed eagerly during cascade. Auto-fill/auto-fit `repeat()` defer expansion to track-sizing time when container size is known.

---

## 5. Test strategy

Mirrors the flex test layout (cycle-1 ships ~6 direct-construction tests + ~2 production-pipeline tests):

- **Direct-construction tests** in `FlexLayouterTests.cs`-style file `GridLayouterTests.cs`:
  - Construct `Box` trees by hand (no HTML parsing).
  - Pin exact item fragment offsets + sizes per cycle's CSS feature set.
  - Each cycle adds ~6-10 tests.
- **Production-pipeline tests** in `GridLayouterProductionTests.cs`:
  - HTML → BlockLayouter → GridLayouter end-to-end.
  - Verify dispatch + cycle-feature integration.
- **W3C CSS conformance tests** (per phase doc §"GridLayouter | W3C subset | Run grid spec examples + WPT grid tests"):
  - Integrated in `tests/NetPdf.W3cConformance/` (= existing test project).
  - Cycle 1 starts with a small pinned subset; subsequent cycles expand it.

Per Task 17 / 18 reaching feature completeness, expect ~80-120 grid-specific tests.

---

## 6. Risks + dependencies

### Architectural risks

- **Intrinsic sizing dependency (cycle 3+)**: `auto` / `min-content` / `max-content` track sizing requires content measurement (= the L19 underlying deferral that also blocks `min-width: auto` for flex items and `flex-basis: content`). Cycle 3's MVP approximation may need to ship with documented inaccuracy; full closure waits for L19.
- **Two-pass track sizing**: the algorithm's iterative "growth limit hits force redistribution" can have O(item-count × track-count × items-per-iteration) cost. Phase-doc performance budget gates need monitoring (= 3-page invoice ≤ 200 ms p50). Profile early.
- **GridContinuation cache shape**: `TrackSizingCache` is `object?` (= opaque) per the current type. The cycle-6 design needs to pick a concrete cache shape that survives chain serialization + doesn't bloat `LayoutContinuation` allocations.

### CSS parsing risks

- The CSS Grid grammar is complex (`<track-list>` has nested `repeat()`, `minmax()`, named lines `[name]` interleaved with track entries, etc.). Cycle 1's pixel-tracks-only scope sidesteps most of this; cycle 4's `repeat()`/`minmax()` integration is the biggest parser lift.
- `grid` shorthand (= simultaneous template-rows + template-columns + auto-flow) is a separate lift (post-cycle-7).

### Clean-room policy reminder (per `docs/clean-room-policy.md` + `phase-3-layout-and-pagination.md` line 111)

- Read Servo's `layout_2020/grid` and Taffy's grid implementation **for understanding** the algorithm interpretation only — NOT for transliteration.
- Leave a one-line note in `GridLayouter.cs` for each external implementation consulted.
- Implementation derives from CSS Grid L1 §11 spec text.

### Cross-layouter coordination

- Item alignment (`justify-items`, `align-items`, etc.) shares the `<self-position>` decoder with FlexLayouter — refactor (L9 hardening F#3) already shipped the shared helper.
- `<baseline-position>` alignment in grid cells: same deferral as flex L18.

---

## 7. Implementation order summary

| Cycle | Title | CSS slice | Algorithm | Tests | Est. |
|---|---|---|---|---|---|
| 1 | Hello World | explicit `<length>` tracks + integer placement | trivial (sum tracks) | direct + production | 2-3 d |
| 2 | `fr` units | `<flex>` tracks | Step 4 | direct + edge cases | 1.5 d |
| 3 | `auto` tracks | `auto` / `min-content` / `max-content` | Steps 1-3 (intrinsic) | + L19-approximation known-gap | 3 d |
| 4 | `minmax()`/`fit-content()`/`repeat(int, ...)` | CSS functions | growth-limit integration | + parser tests | 2 d |
| 5 | auto-placement + `span` | `<grid-line>: span N` + auto | placement algorithm sparse | + sparse-flow tests | 2 d |
| 6 | multi-page grid | continuation chain | row-boundary pagination | + multi-page production | 2 d |
| 7 | named areas + auto-fill/auto-fit + dense | `grid-template-areas`, `repeat(auto-...)`, `grid-auto-flow: dense` | placement algorithm dense + container-derived count | + named-area tests | 2 d |

**Estimated total:** ~14-16 days across 7 cycles. Matches the phase-plan budget (Task 17 = 8 d + Task 18 = 5 d) reasonably; cycles 6 + 7 partially overlap with Task 18.

---

## 8. Done criteria for the Task 17 + 18 series

- All 7 cycles shipped via separate PRs (each merged after Roland review per the standing workflow).
- W3C grid spec sample tests pass (subset pinned in `tests/NetPdf.W3cConformance/`).
- 3-page invoice render still hits ≤ 200 ms p50 (= performance gate per CLAUDE.md cross-cutting rule #8).
- AOT/JIT byte-parity preserved across all PRs.
- All deferrals from the cycle plan + the "out-of-scope follow-ons" section above tracked in `docs/deferrals.md`.

---

## 9. Open design questions (for Roland review)

1. **Cycle granularity** — Are 7 cycles right? Earlier tasks (15, 16) used ~15-20 sub-cycles (L1-L17 + cycle 4a-4e). Grid's CSS feature set is large enough that smaller cycles may be warranted; e.g., cycle 3 (intrinsic sizing) could split into cycle 3a (Step 1 + 2) + cycle 3b (Step 3 + maximize).
2. **L19 dependency** — Cycle 3 needs content measurement for accurate `auto` tracks. Two options: (a) ship cycle 3 with documented approximation (= zero contribution for non-dimensioned items, matching the flex `min-width: auto` approximation pattern); (b) defer cycle 3 until L19 ships content measurement. Option (a) ships more value sooner + matches existing flex deferral pattern.
3. **CSS Grid L2 vs L1** — phase doc references Grid L1 ("Level 1 only — subgrid is post-v1"). Confirm: L1 is the target spec. (Grid L2 adds subgrid + masonry which are out-of-scope.)
4. **`grid` shorthand** — when to add? Lower priority than the longhands; could fit post-cycle-7 as a small bonus or wait for Task 19-20 housekeeping.
