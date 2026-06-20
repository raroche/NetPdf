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
| Flexbox L1 | **90.9%** (10/11) | ≥ 85% | ✅ met |
| Grid L1 | **80.0%** (8/10) | ≥ 70% | ✅ met |

Three of the four exit criteria are **met**; CSS 2.2 is below its 90% target.
The gates assert a **regression floor** met today (not the aspirational target),
so a passing case breaking turns a gate red while a known gap stays documented
rather than blocking the build.

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

These are tracked in [docs/deferrals.md](../../docs/deferrals.md) /
[docs/compatibility-matrix.md](../../docs/compatibility-matrix.md).

## Layout

- `ConformanceHarness.cs` — HTML → laid-out `BoxFragment` geometry (drives
  `BlockLayouter` through the production `LayoutRetryCoordinator`, multi-page).
- `ConformanceCase.cs` — the case + `BoxExpectation` model + the pass/fail runner
  + per-category `Evaluate` (rate + per-failure report).
- `Css22Cases.cs` / `FragmentationCases.cs` / `FlexboxCases.cs` / `GridCases.cs`
  — the curated case sets.
- `ConformanceGates.cs` — the four `[Fact]` pass-rate gates.

## Adding a case

Append a `ConformanceCase` to the relevant set: an HTML fragment with `id`'d
elements + the spec-correct `BoxExpectation`s (assert only the axes the case is
about). A flex/grid container fills its **parent** content width, so constrain
width via a sized parent, not an explicit `width` on the container.
