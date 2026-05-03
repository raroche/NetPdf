# Performance baseline

NetPdf publishes its measured performance numbers, the targets they back, and a checked-in regression gate. Numbers come from `tests/NetPdf.Benchmarks/` (BenchmarkDotNet 0.14, `[MemoryDiagnoser]` enabled). When new tasks ship, the baseline is re-measured and any regression beyond tolerance is treated as a defect — the targets are the contract.

## Phase 1 targets

From [`docs/phases/phase-1-pdf-writer-and-text.md`](../phases/phase-1-pdf-writer-and-text.md) and `CLAUDE.md`:

| Workload | Target |
|---|---|
| Warm 3-page invoice (HTML+CSS through full pipeline) | ≤ 200 ms p50 |
| Warm 20-page report (tables + images + web fonts) | ≤ 1.5 s p50 |
| 100-page benchmark | < 500 ms |
| Memory growth | linear in page count |
| Process spawning at render time | **forbidden** (architecturally enforced) |

These targets bake in the cost of HTML parsing + CSS cascade + layout + paint. Phase 1 ships only the byte writer (no HTML pipeline yet), so the actual numbers below are massively under target. Phase 2/3 will eat into the headroom; the gap between baseline and target is the budget.

## Honest scope of the captured numbers

**The published baselines below are tiny-fixture writer floors, not representative real-world image-embedding costs.** Every image benchmark uses a 1×1 or 8×8 synthetic image:

- The minimal hand-crafted JPEG / PNG / GIF in `MinimalImageFixtures` / `MinimalPngFixtures` is just enough bytes to satisfy the parser. The wrapper-and-emit cost is dominated by dictionary build + SHA-256 dedup hash.
- The Skia-encoded WebP is a solid-color 8×8 — its decode/re-encode round-trip is trivial compared to a 4K photograph.

A full-resolution photo measures very differently:

- **JPEG passthrough** scales with byte count — the SHA-256 dedup hash is the dominant cost on a multi-MB JPEG, not the dictionary build.
- **PNG/WebP/GIF/AVIF via raster** scales with pixel count + chroma — Skia decode dominates, then the FlateDecode re-encode dominates again.

Realistic image-throughput characterization belongs in a Phase 4+ corpus benchmark with real-world fixtures. The numbers below are valuable for **regression detection** (the writer's overhead per emit is bounded), not for **capacity planning** (how big a real document can be).

## Phase 1 baseline (2026-05-03 capture)

**Environment:** Apple M4 Pro, 14 cores, .NET 10.0.7 / macOS 26.3 / arm64, BenchmarkDotNet 0.14.0. **Workstation GC** (pinned via `<ServerGarbageCollection>false</ServerGarbageCollection>`). `IterationCount=10 WarmupCount=5`. All numbers are **warm** (post-warmup) — cold-start adds JIT compilation + assembly-load cost not measured here.

### Single-page floor

| Benchmark | Mean | Allocated | Ratio vs blank |
|---|---:|---:|---:|
| `BlankSinglePage` (writer floor) | **5.6 µs** | 7.45 KB | 1.00× |
| `SinglePageWithContent` | 5.6 µs | 7.86 KB | 1.00× |
| `SinglePageWithFullMetadata` | 8.2 µs | 8.33 KB | 1.46× |

### Page-count scaling (memory linearity)

| Pages | Blank | With content | Allocated (blank) | Per-page (blank) |
|---:|---:|---:|---:|---:|
| 1 | 5.4 µs | 5.5 µs | 7.45 KB | (base) |
| 10 | 27.7 µs | 29.0 µs | 32.35 KB | 2.49 KB/page |
| 100 | **254.8 µs** | **264.7 µs** | 275.4 KB | 2.45 KB/page |
| 1000 | **2.59 ms** | **2.68 ms** | 2.6 MB | 2.46 KB/page |

Per-page allocation is stable across 4 orders of magnitude (2.45–2.49 KB/page). **O(N) confirmed.**

### Image embedding

| Benchmark | Mean | Allocated | Notes |
|---|---:|---:|---|
| `JpegPassthrough` | 9.2 µs | 11.25 KB | tiny synthetic JPEG; wrapper + dedup hash dominate |
| `PngOpaqueRgb8` | 10.9 µs | 12.04 KB | Predictor 15 passthrough |
| `PngIndexed8BinaryTrns` | 12.2 µs | 12.58 KB | color-key /Mask path |
| `PngRgba8WithSMask` | 19.1 µs | 19.52 KB | alpha-split → 2 indirect XObjects |
| `WebpOpaqueViaRaster` | 19.0 µs | 18.94 KB | Skia decode → RGB → FlateDecode |
| `TransparentGifViaRaster` | 20.0 µs | 19.38 KB | Skia decode → RGB+alpha → 2 indirect XObjects |

AVIF is intentionally omitted from the Phase 1 suite. The macOS SkiaSharp build lacks libavif, which would make the AVIF benchmark host-dependent and break the per-platform pin. Phase 5 cross-platform CI will add AVIF on Linux where libavif is available.

### Dedup costs (split per Task 25 review)

| Benchmark | Mean | Allocated | What it measures |
|---|---:|---:|---|
| `FirstRegistration_AndSave` | 9.4 µs | 11.25 KB | full embed cost: build + hash + allocate slot + Save |
| `CacheHits_Isolated` (per call) | **2.9 µs** | ~970 B/call | inner-loop cost: SHA + dict.TryGetValue (Save excluded) |
| `CacheMisses_100UniqueImages` | 325.7 µs | 1.18 MB | 100 distinct registrations + Save |

The `CacheHits_Isolated` benchmark uses `[OperationsPerInvoke = 99]` so the reported number is per-call, not amortized across an unrelated Save step.

### Streaming output

| Benchmark | Mean | Allocated | Notes |
|---|---:|---:|---|
| `Save_AllocatesArray` (`byte[]`) | 9.2 µs | 11.25 KB | baseline |
| `SaveTo_StreamingBuffer` (`IBufferWriter<byte>`) | 8.9 µs | 12.56 KB | wall-clock ~3% faster; allocations slightly higher because `ArrayBufferWriter`'s grow-by-2× strategy reserves more headroom than the single final `byte[]` does. For very large documents, peak retained working-set differs more — that's where streaming matters. |

### Mixed multi-page document

| Benchmark | Mean | Allocated |
|---|---:|---:|
| `BuildMixedMultiPageDocument` (JPEG + 3 PNG variants + WebP + transparent GIF, 2 pages) | **67.1 µs** | 65.77 KB |

This was previously called "canonical" in the suite; renamed to be honest about what it is: a representative mix of image embed paths, NOT a 1-to-1 mirror of the determinism harness's canonical-everything document.

### Targets vs. actual

| Phase 1 target | Actual (writer-only) | Headroom |
|---|---:|---:|
| 100-page < 500 ms | 264.7 µs | ~1,890× |
| 3-page invoice ≤ 200 ms (writer floor) | ~17 µs | ~12,000× |
| 20-page report ≤ 1.5 s (writer floor) | ~55 µs | ~27,000× |
| Memory growth linear in page count | 2.45 KB/page (stable) | ✓ |

The headroom is the budget for Phase 2 (CSS cascade + computed values), Phase 3 (layout + pagination + paint), and Phase 4 (image decode of real photos, raster fallback for filters). It is generous, but it is a **budget**, not waste.

## Regression gate

The baseline is enforced — not just documented — by `scripts/benchmark-gate.sh`:

```bash
./scripts/benchmark-gate.sh
```

The script:

1. Runs the full benchmark suite (~5 min on M4 Pro), exporting JSON reports per class.
2. Calls the comparison program (`Program.cs --compare BASELINE-DIR CURRENT-DIR [tolerance]`) which loads both directories' `*-report-full-compressed.json`, extracts each benchmark's `Statistics.Mean`, computes the ratio current/baseline, and exits 1 if any benchmark exceeds the tolerance (default **+25%**, override via `BENCHMARK_GATE_TOLERANCE` env var).
3. Propagates the comparison exit code: 0 = no regression past tolerance, 1 = regression detected, 2 = environmental error.

**Negative-path verified**: I synthetically halved one baseline value and re-ran the comparison — the gate correctly reported `BlankSinglePage 2.8 us → 5.6 us 2.00× FAIL` and exited 1. The gate has teeth, not just procedure.

The baseline is committed under `tests/NetPdf.Benchmarks/baselines/phase-1-osx-arm64/` (one JSON per benchmark class). When new platforms are pinned (Phase 5), each gets its own subdirectory keyed by `os-arch`. The gate script auto-detects the running platform from `uname` and selects the right baseline.

### Re-baseline protocol

A regression here may be expected (a new feature legitimately added work) or accidental. The protocol:

1. **Don't re-baseline silently.** Investigate the cause first.
2. **Verify the change is in the right direction.** A new layout pass that adds 50 µs to canonical-document time during Phase 3 is expected. A Phase 1 hardening task that adds 50 µs without justification is not.
3. **`./scripts/benchmark-gate.sh capture`** runs the suite and overwrites the platform's baseline. Use ONLY when re-baselining is deliberate. The git diff of the baseline JSON files documents the cause; commit message explains it.

## Run protocol

### Inner loop (single benchmark, fast)

```bash
dotnet run --project tests/NetPdf.Benchmarks -c Release -- --filter "*PageScaling*"
```

The `--filter` glob accepts BDN's standard wildcards. Use `--list flat` to see all benchmark IDs.

### Full Phase 1 suite (~5 min)

```bash
dotnet run --project tests/NetPdf.Benchmarks -c Release -- --filter "*"
```

The Markdown summary table prints to stdout at the end; full BDN artifact tree (HTML, CSV, Markdown, JSON) lands in `BenchmarkDotNet.Artifacts/results/`.

### Local regression check (~5 min)

```bash
./scripts/benchmark-gate.sh
```

### Custom comparison

```bash
# Compare any two directories of BDN JSON exports.
dotnet run --project tests/NetPdf.Benchmarks -c Release -- \
  --compare /path/to/baseline /path/to/current 1.10
```

(Tolerance argument is the max ratio current/baseline before failure; 1.10 = +10%.)

## Known scope choices

- **Workstation GC pinned**: `<ServerGarbageCollection>false</ServerGarbageCollection>` in the benchmarks csproj. Server GC (CI default) produces measurably different allocation/timing profiles due to per-core GC arenas; without pinning, the same baseline numbers wouldn't hold across hosts. Documented; CI containers will inherit the pin via the csproj.
- **No cold-start measurement** in Phase 1. CLI users (e.g., invoice CLI generating one PDF per invocation) pay JIT compilation + assembly-load + first-image-cache-warmup costs not measured here. Adding cold-start coverage is post-Phase-1 scope.
- **No `[HardwareCounters]` profiling**: branch mispredict / cache miss rates are useful for hot-loop tuning but Phase 1 doesn't need them. Defer to Phase 2/3 when layout perf matters.
- **Single-threaded only**: concurrent rendering is not a Phase 1 feature. Multi-document parallel-build benchmarks come post-v1.
- **Skia-encoded WebP is NOT byte-stable** across SkiaSharp versions. Acceptable for benchmark timing (which only requires consistent input bytes within a single suite run), unsuitable for byte-determinism pinning. Hand-crafted JPEG/PNG/GIF fixtures are used elsewhere in the determinism story.

## Comparison with target ecosystem

For context on what these numbers mean (warm path, single PDF):

- **wkhtmltopdf** (the PDF backend many invoiced systems still ship): reportedly ~5–15 seconds for a 20-page invoice on a comparable laptop, much of it spent in the bundled QtWebKit subprocess.
- **Chromium-headless / Playwright print**: ~200–800 ms for a 1-page render once the browser is warm; cold starts add 1–2 s.
- **PDFsharp** (pure .NET, similar scope): ~50–100 µs for a blank page — same order of magnitude as NetPdf's writer.

The benchmark above proves NetPdf's *writer* is in the same regime as PDFsharp; the headroom for Phase 2/3 layout work is enormous compared to browser-print pipelines.
