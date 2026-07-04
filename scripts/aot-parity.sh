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

# Detect the host RID so AOT publish doesn't need cross-compile setup.
# On Linux, distinguish glibc from musl — `uname` reports `Linux-x86_64` for both, but
# .NET ships separate runtimes (linux-x64 vs linux-musl-x64). A binary built for one
# won't run on the other, so the parity gate on Alpine / musl-based CI must pick the
# musl RID. Detection: `getconf GNU_LIBC_VERSION` succeeds on glibc; on musl the
# canonical sentinel is the presence of `/lib/ld-musl-*.so.1`.
detect_linux_libc() {
  if [ -f /lib/ld-musl-x86_64.so.1 ] || [ -f /lib/ld-musl-aarch64.so.1 ] \
     || [ -f /lib/ld-musl-armhf.so.1 ]; then
    echo musl
  elif command -v getconf >/dev/null 2>&1 && getconf GNU_LIBC_VERSION >/dev/null 2>&1; then
    echo glibc
  elif ldd --version 2>&1 | grep -qi musl; then
    echo musl
  else
    echo glibc # default assumption — most Linux CI runners are glibc.
  fi
}

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) HOST_RID="osx-arm64" ;;
  Darwin-x86_64) HOST_RID="osx-x64" ;;
  Linux-x86_64)
    if [ "$(detect_linux_libc)" = "musl" ]; then HOST_RID="linux-musl-x64"
    else HOST_RID="linux-x64"; fi
    ;;
  Linux-aarch64)
    if [ "$(detect_linux_libc)" = "musl" ]; then HOST_RID="linux-musl-arm64"
    else HOST_RID="linux-arm64"; fi
    ;;
  *) echo "error: unsupported host platform $(uname -s)-$(uname -m)" >&2; exit 4 ;;
esac

echo "==> Publishing AOT smoke binary for ${HOST_RID} (restore + native publish)"
# Single restore+publish with PublishAot. An earlier version split restore (WITHOUT
# PublishAot) from a `--no-restore` publish (WITH PublishAot) to dodge NETSDK1207 on the
# netstandard2.0 SourceGen analyzer that the smoke project references transitively via
# NetPdf.Css. That split is obsolete AND harmful: without PublishAot at restore, the
# `Microsoft.DotNet.ILCompiler` package is never restored, so the publish silently degrades
# to a trimmed MANAGED publish ("Optimizing assemblies for size" but no "Generating native
# code") and emits NO native binary — green locally only when a prior AOT publish warmed the
# NuGet/obj cache, but red on a clean CI checkout (exit 3 below). NETSDK1207 is already
# prevented by the `SetTargetFramework=netstandard2.0` pin on NetPdf.Css's SourceGen
# ProjectReference, so a single PublishAot restore+publish is both correct and clean.
dotnet publish "$SMOKE_PROJ" \
  -c Release \
  -f net10.0 \
  -r "$HOST_RID" \
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
