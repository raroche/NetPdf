#!/usr/bin/env bash
# Copyright 2026 Roland Aroche and NetPdf contributors.
# Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
#
# AOT/JIT parity gate. Publishes the AOT smoke binary, then runs the parity tests
# in NetPdf.UnitTests which compare the AOT binary's output against the JIT factory's
# output and fail on any byte difference.
#
# Use this locally before claiming AOT parity, and wire it into CI as a single step
# that gates merge. Exit code propagates: 0 if everything matches, non-zero on any
# publish, run, or assertion failure.

set -euo pipefail

# Repo root = directory containing NetPdf.slnx; resolved relative to this script.
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ ! -f "NetPdf.slnx" ]; then
  echo "error: scripts/aot-parity.sh must be run from a checkout containing NetPdf.slnx (got cwd: $REPO_ROOT)" >&2
  exit 2
fi

ARTIFACTS_DIR="$REPO_ROOT/artifacts/aot-smoke"
SMOKE_PROJ="$REPO_ROOT/tests/NetPdf.AotSmoke/NetPdf.AotSmoke.csproj"

echo "==> Publishing AOT smoke binary"
dotnet publish "$SMOKE_PROJ" \
  -c Release \
  -f net10.0 \
  -p:PublishAot=true \
  -o "$ARTIFACTS_DIR" \
  --nologo

if [ "$(uname -s)" = "Linux" ] || [ "$(uname -s)" = "Darwin" ]; then
  AOT_BIN="$ARTIFACTS_DIR/NetPdf.AotSmoke"
else
  AOT_BIN="$ARTIFACTS_DIR/NetPdf.AotSmoke.exe"
fi

if [ ! -x "$AOT_BIN" ] && [ ! -f "$AOT_BIN" ]; then
  echo "error: expected AOT binary not found at $AOT_BIN after publish" >&2
  exit 3
fi

echo "==> Running native AOT smoke (sanity check)"
"$AOT_BIN"

echo "==> Running JIT/AOT parity tests"
dotnet test "$REPO_ROOT/tests/NetPdf.UnitTests/NetPdf.UnitTests.csproj" \
  -c Release \
  --filter "FullyQualifiedName~AotJitParityTests" \
  --logger "console;verbosity=normal" \
  --nologo

echo "==> AOT/JIT parity verified."
