#!/usr/bin/env bash
# Copyright 2026 Roland Aroche and NetPdf contributors.
# Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
#
# Performance regression gate. Runs the BenchmarkDotNet suite, exports JSON, and
# diffs against the committed per-platform baseline. Exit code propagates:
#   0 = no regression beyond the tolerance
#   1 = at least one benchmark regressed past the threshold
#   2 = environmental error (publish failed, baseline missing for this platform, etc.)
#
# Use locally before committing perf-relevant changes; Phase 5 wires this into
# the CI matrix as a single merge gate.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

BENCH_PROJ="$REPO_ROOT/tests/NetPdf.Benchmarks/NetPdf.Benchmarks.csproj"
BASELINE_ROOT="$REPO_ROOT/tests/NetPdf.Benchmarks/baselines"
ARTIFACTS_DIR="$REPO_ROOT/BenchmarkDotNet.Artifacts/results"
TOLERANCE="${BENCHMARK_GATE_TOLERANCE:-1.25}"   # +25% default; override via env

# Resolve the platform key (matches DeterminismDiagnostics.CurrentPlatformKey shape).
case "$(uname -s)" in
  Linux*)   OS=linux ;;
  Darwin*)  OS=osx ;;
  CYGWIN*|MINGW*|MSYS*)  OS=win ;;
  *)        OS=unknown ;;
esac
case "$(uname -m)" in
  x86_64|amd64)  ARCH=x64 ;;
  arm64|aarch64) ARCH=arm64 ;;
  *)             ARCH="$(uname -m)" ;;
esac
PLATFORM_KEY="${OS}-${ARCH}"
BASELINE_DIR="$BASELINE_ROOT/phase-1-${PLATFORM_KEY}"

if [ ! -d "$BASELINE_DIR" ]; then
  echo "warn: no baseline pinned for platform '$PLATFORM_KEY' at $BASELINE_DIR" >&2
  echo "      Capture one with: ./scripts/benchmark-gate.sh capture" >&2
  exit 2
fi

# 'capture' subcommand: run + replace the baseline (used during deliberate
# re-baselining; never run by CI).
if [ "${1:-}" = "capture" ]; then
  echo "==> Capturing new baseline for $PLATFORM_KEY"
  rm -rf "$ARTIFACTS_DIR"
  dotnet run --project "$BENCH_PROJ" -c Release -- --filter "*" --exporters JSON
  rm -rf "$BASELINE_DIR"
  mkdir -p "$BASELINE_DIR"
  cp "$ARTIFACTS_DIR"/*-report-full-compressed.json "$BASELINE_DIR/"
  echo "==> Baseline written to $BASELINE_DIR"
  echo "    Review the diff in git, verify the changes are deliberate, then commit."
  exit 0
fi

echo "==> Running benchmark suite ($PLATFORM_KEY)"
rm -rf "$ARTIFACTS_DIR"
dotnet run --project "$BENCH_PROJ" -c Release -- --filter "*" --exporters JSON

echo "==> Comparing against baseline at $BASELINE_DIR (tolerance ${TOLERANCE})"
# `set -e` (top of script) would otherwise kill the script the instant `--compare`
# returns non-zero on a regression — never reaching the friendly messaging or the
# explicit `exit`. Wrap in `if`/`else` so we capture the exit code under shell
# semantics that don't trigger errexit.
if dotnet run --project "$BENCH_PROJ" -c Release --no-build -- \
    --compare "$BASELINE_DIR" "$ARTIFACTS_DIR" "$TOLERANCE"; then
  COMPARE_EXIT=0
else
  COMPARE_EXIT=$?
fi

if [ $COMPARE_EXIT -eq 0 ]; then
  echo "==> Performance gate passed."
elif [ $COMPARE_EXIT -eq 1 ]; then
  echo "==> Performance gate FAILED — at least one benchmark regressed past the tolerance." >&2
else
  echo "==> Performance gate errored (exit $COMPARE_EXIT)." >&2
fi
exit $COMPARE_EXIT
