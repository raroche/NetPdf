---
name: bench
description: Run BenchmarkDotNet over NetPdf and compare to the locked baseline. Flag regressions over 10%.
---

# Skill: bench

Drive `tests/NetPdf.Benchmarks` and compare results to the committed baseline. Performance-gate every PR.

## Prerequisites

- Phase 1 has begun — there are real benchmarks to run.
- A baseline JSON exists at `tests/NetPdf.Benchmarks/baseline.json` (committed; updated only via the `release-prep` skill).

## Steps

1. **Build in Release.** Performance numbers from Debug builds are meaningless.
   ```
   dotnet build NetPdf.slnx -c Release
   ```

2. **Run the benchmarks.** All benchmarks unless the user asks for a specific one:
   ```
   dotnet run --project tests/NetPdf.Benchmarks/NetPdf.Benchmarks.csproj -c Release \
     -- --exporters json --artifacts /tmp/netpdf-bench
   ```

3. **Compare to baseline.** For each benchmark in the report:
   - Look up the same `Method` + `Categories` in `tests/NetPdf.Benchmarks/baseline.json`.
   - Compute Δ = `(current_p50 - baseline_p50) / baseline_p50`.
   - Categorize:
     - Δ < +5%: ✅ no regression
     - +5% ≤ Δ < +10%: ⚠️ small regression — note but don't fail
     - Δ ≥ +10%: ❌ regression — fail the gate
     - Δ < -5%: 🚀 improvement — celebrate

4. **Verify the absolute performance gates** from the plan:
   - 3-page invoice ≤ 200 ms p50.
   - 20-page report ≤ 1.5 s p50.
   - Memory growth linear in page count (run `MemoryLinearity` benchmark, verify R² > 0.95 against linear fit).

5. **Print a markdown table** of results:
   ```
   | Benchmark              | Baseline p50 | Current p50 | Δ      | Status |
   |------------------------|--------------|-------------|--------|--------|
   | InvoiceConvert_3Page   | 145 ms       | 152 ms      | +4.8%  | ✅     |
   | ReportConvert_20Page   | 1.21 s       | 1.45 s      | +19.8% | ❌     |
   ```

## Output

Report only. No code changes. If a regression is detected, suggest the most likely culprit (recent commits to hot-path projects: `NetPdf.Layout`, `NetPdf.Paint`, `NetPdf.Pdf`, `NetPdf.Text`).

## Failure modes & responses

- **Benchmark crashes** → run with smaller `--filter` to isolate the failing one.
- **Baseline is missing** → user is running before a baseline was locked. Skip comparison; just print absolute numbers and the gate-vs-target check.
- **Wide variance between runs** → BenchmarkDotNet's MOE > 5%. Re-run with `--launchCount 5` for stability.

## Style notes

- The 200 ms / 1.5 s gates are absolute, the 10% regression gate is relative. Both must pass.
- Don't update the baseline from this skill; that's `release-prep`'s job (bake into a new release tag).
