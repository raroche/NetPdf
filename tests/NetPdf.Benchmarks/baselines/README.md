# BenchmarkDotNet baselines

The performance-regression gate (`scripts/benchmark-gate.sh`) runs the BenchmarkDotNet suite in
`tests/NetPdf.Benchmarks/`, exports the per-benchmark JSON, and diffs each mean against the committed
baseline for the **current platform**, failing on a regression beyond the tolerance (default **+25%**,
overridable via `BENCHMARK_GATE_TOLERANCE`).

## Layout

One directory per baseline set; inside it, one `…-report-full-compressed.json` per benchmark class
(BenchmarkDotNet's `--exporters json` full output). The gate resolves the platform key from `uname`
(`os-arch`, e.g. `osx-arm64`, `linux-x64`, `win-x64`) and picks the matching baseline directory.

```
baselines/
  phase-1-osx-arm64/          # committed — captured on Apple Silicon
    NetPdf.Benchmarks.SinglePageBenchmarks-report-full-compressed.json
    NetPdf.Benchmarks.MixedDocumentBenchmark-report-full-compressed.json
    … (one per benchmark class)
```

## Status

| Platform | Baseline | Owner |
|---|---|---|
| `osx-arm64` | ✅ committed (`phase-1-osx-arm64/`) | dev box |
| `linux-x64` | ⛔ **maintainer step** — capture on the CI runner | maintainer |

The CI benchmark gate runs on `linux-x64`; until a `linux-x64` baseline is committed the gate exits neutral
(the `docs`/`ci` workflows treat exit code 2 as a non-blocking "no baseline yet" notice). Capturing it on a
stable CI runner — not the dev box — keeps the numbers representative of the gated environment.

## Refreshing a baseline

The gate script has a `capture` subcommand that runs the suite and overwrites the current platform's
baseline directory:

```bash
./scripts/benchmark-gate.sh capture
```

Only re-baseline deliberately (after an intended perf change), on a quiet machine, and note the reason in
the commit — a baseline captured on a noisy or differently-specced host will mis-calibrate the gate. See the
re-baseline protocol in [`docs/design/performance.md`](../../../docs/design/performance.md#re-baseline-protocol).

> Coarse, always-on regression guards (p50 latency + allocation-per-page linearity) run on **every**
> `dotnet test` in `tests/NetPdf.UnitTests/Performance/PerformanceGateTests.cs`; those don't need a
> committed baseline. This BenchmarkDotNet flow is the authoritative, fine-grained profile.
