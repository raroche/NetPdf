# Performance baseline

NetPdf publishes its measured performance numbers and the targets they back. Numbers come from `tests/NetPdf.Benchmarks/` (BenchmarkDotNet 0.14, `[MemoryDiagnoser]` enabled). When new tasks ship, the baseline is re-measured and any regression beyond noise is treated as a defect — the targets are the contract, not aspirational.

## Phase 1 targets

From [`docs/phases/phase-1-pdf-writer-and-text.md`](../phases/phase-1-pdf-writer-and-text.md) and `CLAUDE.md`:

| Workload | Target |
|---|---|
| Warm 3-page invoice | ≤ 200 ms p50 |
| Warm 20-page report (tables + images + web fonts) | ≤ 1.5 s p50 |
| 100-page benchmark | < 500 ms |
| Memory growth | linear in page count |
| Process spawning at render time | **forbidden** (architecturally enforced) |

These targets bake in the cost of HTML parsing + CSS cascade + layout + paint. Phase 1 ships only the byte writer (no HTML pipeline yet), so the actual numbers below are massively under target. Phase 2/3 will eat into the headroom; the gap between baseline and target is the budget.

## Phase 1 baseline (2026-05-03 capture)

**Environment:** Apple M4 Pro, 14 cores, .NET 10.0.7 / macOS 26.3 / arm64, BenchmarkDotNet 0.14.0. Workstation GC. `IterationCount=5 WarmupCount=3`.

| Benchmark | 1 page | 10 pages | 100 pages | Allocations (100 pages) |
|---|---:|---:|---:|---:|
| Single blank A4 page → bytes | 5.46 µs | — | — | 7.45 KB |
| N blank pages → bytes | 5.42 µs | 27.6 µs | **253.5 µs** | 275.4 KB |
| N pages with simple content stream | 5.47 µs | 28.5 µs | **267.5 µs** | 317.4 KB |
| JPEG passthrough (1 image, 1 page) | 9.28 µs | — | — | 11.25 KB |
| Transparent GIF via raster (alpha-split SMask) | 19.4 µs | — | — | 19.38 KB |
| Image dedup: register same JPEG 100× | 74.7 µs | — | — | 96.33 KB |
| Canonical multi-page document | 30.0 µs | — | — | 26.56 KB |

(Single-image and dedup benchmarks are not page-count-parameterized; the 1/10/100 columns repeat the same number for those rows in the raw BDN output.)

### Targets vs. actual

| Phase 1 target | Actual | Headroom |
|---|---:|---:|
| 100-page document < 500 ms | **267 µs** | ~1,870× |
| 3-page invoice ≤ 200 ms (writer only — no HTML/layout/text shaping yet) | ~16 µs | ~12,500× |
| 20-page report ≤ 1.5 s (writer only) | ~55 µs | ~27,000× |
| Memory growth linear in page count | **~2.5 KB/page** (linear) | ✓ |

The headroom is the budget for Phase 2 (CSS cascade + computed values) and Phase 3 (layout + pagination + paint). Each percentage point of regression burned at this layer should be deliberate, not accidental.

### Memory linearity verification

| Page count | Total allocated | Delta vs. previous | Per-page |
|---:|---:|---:|---:|
| 1 | 7.45 KB | — | (base) |
| 10 | 32.35 KB | 24.9 KB | 2.49 KB/page |
| 100 | 275.4 KB | 243.05 KB | 2.45 KB/page |

Per-page allocation is stable across the 1×10× scale jump (2.49 → 2.45 KB), confirming the **memory grows linearly with page count** invariant. Future regressions that introduce O(N²) per-page work would cause the per-page number to climb with N and surface here immediately.

### Image-path observations

- **JPEG passthrough is 9.3 µs**: dominant cost is the dictionary build + SHA-256 dedup hash; the JPEG bytes themselves go through unchanged.
- **Transparent GIF via raster is 19.4 µs**: ~10 µs more than JPEG because of the SkiaSharp decode + RGBA → RGB plane + alpha plane re-encode (FlateDecode at `CompressionLevel.SmallestSize`).
- **Image dedup (100 calls on the same image) is 74.7 µs total**: ~750 ns per cache-hit registration. Each call hashes the dict + payload and a single `Dictionary.TryGetValue`; no XObject duplication.
- **Canonical document is 30 µs**: full Phase 1 surface in one document — JPEG dedup, transparent GIF SMask wiring, multi-page, metadata, content-stream operators.

## Run protocol

### Inner loop (single benchmark, fast)

```bash
dotnet run --project tests/NetPdf.Benchmarks/NetPdf.Benchmarks.csproj \
  -c Release \
  -- --filter "*JPEG*"
```

The `--filter` glob accepts BDN's standard wildcards. Use `--list flat` to see all benchmark IDs.

### Full Phase 1 suite (~3 min)

```bash
dotnet run --project tests/NetPdf.Benchmarks/NetPdf.Benchmarks.csproj \
  -c Release \
  -- --filter "*"
```

The Markdown summary table prints to stdout at the end; copies of the full BDN artifact tree (HTML, CSV, Markdown) land in `BenchmarkDotNet.Artifacts/results/`. Re-paste the summary table into [docs/design/performance.md](performance.md) (this file) when re-baselining after a phase ships.

### CI gate (Phase 5 wiring)

The Phase 5 CI matrix will run a regression-gated short profile (warmup=1, iter=3) on each platform, compare median against the committed baseline, and fail if any benchmark regresses by more than +25%. Until that lands, the suite is an on-demand local check; this file's table is the authoritative reference.

## Re-baselining protocol

A regression here may be expected (a new feature legitimately added work to the writer) or accidental (a code change inadvertently allocated more, took more passes, etc). The protocol:

1. **Don't re-baseline silently.** If the numbers change, write down WHY before updating this file. The git log of this file should explain the cause for every shift.
2. **Verify the change is in the right direction.** If a Phase 2 task ships and adds 50 µs to canonical-document time, that's expected (more work). If a Phase 1 hardening task adds 50 µs without justification, investigate.
3. **Keep the headroom honest.** When a change tightens a target's headroom, note it in the table so the team sees the budget shrinking.

## Known shape choices

- **Hand-crafted minimal JPEG / GIF**: the benchmarks use the same byte-stable inline fixtures the AOT smoke uses (no test-fixture dependency). This keeps the benchmark numbers stable across runs and makes the suite trivially portable.
- **`MemoryDiagnoser` is on**: every method's allocation cost is measured. Important for the "memory grows linearly" invariant; also catches accidental boxing or LINQ-in-a-hot-path regressions.
- **`PageCount` parameter is global**: BDN runs every benchmark for each value of `[Params(1, 10, 100)]`, even those that don't use the parameter (single-image, dedup, canonical). The wasted runs are cheap (~1 minute total) and isolating non-parameterized benchmarks into a separate class is a Phase 5 cleanup if the suite grows past this trade-off.

## Comparison with target ecosystem

For context on what these numbers mean:

- **wkhtmltopdf** (the PDF backend most invoiced systems still ship): reportedly ~5-15 seconds for a 20-page invoice on a comparable laptop, much of it spent in the bundled QtWebKit subprocess.
- **Chromium-headless / Playwright print**: ~200-800 ms for a 1-page render once the browser is warm; cold starts add 1-2 s.
- **PDFsharp** (pure .NET, similar scope): ~50-100 µs for a blank page, similar order of magnitude as NetPdf's byte writer.

The benchmark above proves NetPdf's *writer* is in the same regime as PDFsharp; the headroom for Phase 2/3 layout work is enormous compared to browser-print pipelines.
