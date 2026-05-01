---
name: aot-check
description: Verify Native AOT publish + execution still works for NetPdf. Run after any change that might use reflection or dynamic code.
---

# Skill: aot-check

NetPdf is required to be Native AOT-compatible. The `NetPdf.AotSmoke` project publishes as a self-contained native binary; if anything in the dependency graph or our code regresses AOT, this skill catches it.

## When to invoke

- After adding a new dependency.
- After adding a serialization path (`System.Text.Json`, etc.).
- After heavy refactoring of the public API or anything that interacts with reflection.
- Always run as part of CI pre-merge.

## Steps

1. **Determine the host platform** (`linux-x64`, `linux-musl-x64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`). On macOS Apple Silicon: `osx-arm64`.

2. **Publish the smoke project as Native AOT:**
   ```
   dotnet publish tests/NetPdf.AotSmoke/NetPdf.AotSmoke.csproj \
     -c Release -r <rid> -p:PublishAot=true -o /tmp/netpdf-aot
   ```

3. **Inspect publish output for warnings.** AOT analyzer warnings (IL2026, IL2050, IL3050, IL3053) are **errors** for our project (TreatWarningsAsErrors=true). If any appear, the publish should have failed; if it succeeded with warnings, something is mis-configured.

4. **Run the published binary:**
   ```
   /tmp/netpdf-aot/NetPdf.AotSmoke
   ```
   Expected output: `NetPdf.AotSmoke phase=<N> ok` (where `<N>` is the current phase).
   Expected exit code: `0`.

5. **(Phase 1+) Verify functional output.** Once the smoke project does real work (Phase 1 onward writes a tiny PDF), assert the output bytes are non-empty and structurally valid:
   ```
   /tmp/netpdf-aot/NetPdf.AotSmoke /tmp/netpdf-aot/out.pdf
   qpdf --check /tmp/netpdf-aot/out.pdf
   ```

6. **Compare binary size to baseline.** AOT publish should be ~30-60 MB depending on platform. If it suddenly grows past 100 MB, something pulled in too much (e.g., the full ICU dataset from `globalization-invariant=false`). The smoke project sets `<InvariantGlobalization>true</InvariantGlobalization>` — verify it's still set.

7. **Print a summary:**
   ```
   AOT smoke (<rid>):
     Publish:       ✅
     Warnings:      0
     Run:           ✅ (exit 0)
     Output PDF:    ✅ (qpdf check clean)
     Binary size:   42 MB (baseline: 40-50 MB)
   ```

## Failure modes & responses

- **`IL2026` / `IL3050` warning** → some code path uses `RequiresUnreferencedCode` or `RequiresDynamicCode`. Identify the call site (warning text includes it). Either:
  - Remove the call (most cases).
  - Wrap with `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` and ensure no NetPdf-public-API path reaches it.
  - Replace with a source-generator-based alternative.
- **Publish succeeds but binary fails to run** → likely a P/Invoke loading issue (HarfBuzz / Skia native asset missing for this RID). Verify `HarfBuzzSharp.NativeAssets.<rid>` and `SkiaSharp.NativeAssets.<rid>` are referenced.
- **`InvalidOperationException: Invariant globalization is enabled`** → the smoke project doesn't allow non-invariant culture. Either disable invariant mode for the smoke (post-v1) or refactor the failing code to be culture-invariant.
- **Binary size > 100 MB** → check what was pulled in. Common cause: ICU data, full reflection-emit, or accidentally referencing a heavy package.

## Output

Report only. No code changes. If AOT regresses, suggest the most likely PR/commit that introduced it (heuristic: recent changes to AssemblyInfo, dependency graph, or anything using `Activator`/`Reflection`).

## Style notes

- AOT compatibility is non-negotiable — the project's value is partly in being deployable as a native binary.
- "Works on JIT, broken on AOT" is the exact failure mode this skill catches. Always run on both.
