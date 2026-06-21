<!--
Copyright 2026 Roland Aroche and NetPdf contributors.
Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
-->

# NetPdf W3C conformance suite

Phase 3 exit criteria **3–6** — the measured CSS-layout conformance of the
NetPdf engine. This replaces the prior smoke stub (`Solution_Compiles`) with a
**curated assertion suite**: each case is an HTML fragment plus the spec-correct
border-box geometry the layout must produce, and a per-category runner computes
a **pass-rate**.

## Pass-rates (measured 2026-06-21)

| Category | Pass-rate | Roadmap target | Status |
|---|---|---|---|
| CSS 2.2 layout | **96.7%** (29/30) | ≥ 90% | ✅ met |
| Fragmentation | **90.0%** (9/10) | ≥ 80% | ✅ met |
| Flexbox L1 | **100%** (19/19) | ≥ 85% | ✅ met |
| Grid L1 | **100%** (15/15) | ≥ 70% | ✅ met |

**All four conformance exit criteria are met.** The post-`0.7.0-beta` sizing-residuals
PR raised three categories: Grid to **100%** (`fr` tracks now subtract the gutters
from their free space), CSS 2.2 to **96.7%** (percentage min/max-width/height resolve
against the containing block), and Flexbox stays **100%** with a new percentage-gap
case (`column-gap`/`row-gap` percentages resolve against the container content box).
Earlier: the flex/grid gap PR took Flexbox to 100% + Grid fixed-track gaps to 100%,
and the box-model PR took CSS 2.2 to 93.3% (`box-sizing: border-box` block axis +
`LengthPx` min/max). The one remaining CSS 2.2 gap is auto-height shrink-to-fit
(see below).

## How the gate works — per-case baseline, not a pass-rate floor

The pass-rate above is **published measurement only**. The actual regression
gate is **per-case** (`ConformanceGates`): every case with no `KnownGap` marker
**must pass**, and every `KnownGap` case **must still fail**. So:

- A previously-passing case breaking → the gate goes red (a regression).
- A `KnownGap` case starting to pass → the gate goes red too, telling you to
  drop the marker and re-publish the higher rate (a gap closing is good news,
  and the gate makes sure the docs get updated rather than the win slipping by).

A loose pass-rate floor couldn't do this: a passing case could break while a
known-failing case started passing, leaving the aggregate unchanged and CI
green. The per-case baseline closes that hole (PR 1 review [P1]).

Two further harness guards back the geometry assertions:

- **Content-omission diagnostics fail a case.** The harness captures pipeline
  diagnostics; a `LAYOUT-INLINE-SKIPPED-*` / `*-UNSUPPORTED-*` diagnostic means
  an element was silently dropped (the canonical hazard: every case runs
  `shaperResolver: null`, so accidental text content would be skipped), which a
  geometry assertion can't always catch. Approximation / overflow diagnostics
  (forced-overflow, grid-fr-under-indefinite) are *not* fatal — that content is
  emitted, so the geometry checks validate it directly (PR 1 review [P2]).
- **Duplicate / partial output fails a case.** Each expectation asserts the
  fragment count for its `(id, page)` (default 1) — a duplicate emission is a
  failure, not a silent first-match (PR 1 review [P3]) — and a render that
  exhausts `maxPages` with layout still incomplete fails rather than asserting
  on partial output.

## Why curated (not vendored WPT)

Decided 2026-06-20: a curated NetPdf assertion suite over vendoring real
web-platform-tests. It fits the clean-room + deterministic + AOT ethos — stable
pass-rates, no external-license / repo-size / reftest-image-flakiness baggage
against a non-browser engine. The harness drives the **internal** layout
pipeline (`Phase2Pipeline` box tree → `BlockLayouter`) and asserts `BoxFragment`
geometry directly; the public facade exposes only PDF bytes and the repo has no
content-stream reader, so fragment-level assertions are both cleaner and the
only viable path. Cases assert block-box geometry (sized elements, no text
metrics) so they're deterministic without a font dependency.

## Known gaps (the failing cases — each is a real, documented approximation)

- **`auto` height shrink-to-fit** (CSS 2.1 §10.6.3) — NetPdf emits an auto block
  height of `0 + padding + border`. Sibling placement + pagination already use
  the subtree visual extent (flow is correct), but the box's own emitted
  background/border under-sizes. Growing the EMITTED height destabilizes
  multi-page block-flow pagination (forced-overflow instead of clean splits), so
  it stays deferred — see `auto-height-emit-vs-pagination` in
  [docs/deferrals.md](../../docs/deferrals.md).
- **`break-before: page`** (Fragmentation §3.1) — forced-break metadata isn't
  propagated from the box yet.

**Closed by the sizing-residuals PR (post-`0.7.0-beta`):** grid `fr` tracks subtract
the gutters from their distributed free space (Grid → 100%); percentage min/max-width/
height resolve against the containing block (CSS 2.2 → 96.7%); percentage `column-gap`/
`row-gap` resolve against the container content box (the new `flex-percentage-column-gap`
case). **Closed by the flex/grid gap PR:** flex + grid `gap` / `column-gap` / `row-gap`
length gutters + an explicit `width` on a flex/grid container. **Closed earlier
(box-model PR):** `box-sizing: border-box` (block-axis emit + subtree measure + floats)
and `LengthPx` min/max-width/height clamping on explicit AND auto/fill sizes.

These are tracked in [docs/deferrals.md](../../docs/deferrals.md) /
[docs/compatibility-matrix.md](../../docs/compatibility-matrix.md).

## Layout

- `ConformanceHarness.cs` — HTML → laid-out `BoxFragment` geometry (drives
  `BlockLayouter` through the production `LayoutRetryCoordinator`, multi-page) +
  captures diagnostics + flags `maxPages` exhaustion.
- `ConformanceCase.cs` — the case + `BoxExpectation` model (incl. `KnownGap` and
  per-`(id, page)` fragment count) + the pass/fail runner + per-category
  `Evaluate` (regressions, closed gaps, published rate, per-case report).
- `Css22Cases.cs` / `FragmentationCases.cs` / `FlexboxCases.cs` / `GridCases.cs`
  — the curated case sets.
- `ConformanceGates.cs` — the four `[Fact]` per-case-baseline gates.

## Adding a case

Append a `ConformanceCase` to the relevant set: an HTML fragment with `id`'d
elements + the spec-correct `BoxExpectation`s (assert only the axes the case is
about). A flex/grid container fills its **parent** content width, so constrain
width via a sized parent, not an explicit `width` on the container.

If a case documents an engine gap and is **expected to fail**, tag it with
`KnownGap: "<reason>"` — the gate then guards it from the opposite direction
(when the gap closes the gate goes red so the marker + published rate get
updated). When you fix the gap, remove the marker and bump the rate here.
