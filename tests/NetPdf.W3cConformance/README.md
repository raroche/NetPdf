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

## Pass-rates (measured 2026-06-20)

| Category | Pass-rate | Roadmap target | Status |
|---|---|---|---|
| CSS 2.2 layout | **84.2%** (16/19) | ≥ 90% | ⚠️ below — documented gaps |
| Fragmentation | **90.0%** (9/10) | ≥ 80% | ✅ met |
| Flexbox L1 | **83.3%** (10/12) | ≥ 85% | ⚠️ below — documented gaps |
| Grid L1 | **80.0%** (8/10) | ≥ 70% | ✅ met |

Two of the four exit criteria are **met** (Fragmentation, Grid); CSS 2.2 and
Flexbox are below their targets with **documented gaps** (listed below).

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

- **`auto` height shrink-to-fit** (CSS 2.1 §10.6.3) — NetPdf resolves an auto
  block height to `0 + padding + border` (sibling placement uses the subtree
  visual extent, so flow is correct; the box's own background/border under-size).
- **`box-sizing: border-box`** — declared width/height are treated as content-box.
- **min/max sizing on an explicit width** (§10.4) — `min-width`/`max-width` don't
  clamp an explicit `width`.
- **`break-before: page`** (Fragmentation §3.1) — forced-break metadata isn't
  propagated from the box yet.
- **`gap` / `column-gap` / `row-gap`** on flex + grid containers — gutters aren't
  inserted between tracks/items.
- **explicit `width` on a flex/grid container** — the container ignores its own
  `width` and fills the parent content width instead (the rest of the suite
  works around this by nesting in a sized parent; `flex-explicit-container-width`
  exercises it head-on so the Flexbox rate isn't inflated by avoiding it).

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
