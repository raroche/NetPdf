# AOT smoke test

NetPdf is **AOT-clean**: every code path used by `HtmlPdf.Convert` (and the underlying `PdfDocument` byte writer) compiles under .NET Native AOT without trim warnings, runs in the published native binary, and produces byte-identical PDF output to the equivalent JIT run.

This is enforced by `tests/NetPdf.AotSmoke/` — a console application that:

1. Builds a small representative PDF document (multi-page, metadata, image with content-hash dedup, both `AppendContent` overloads, deterministic `CreationDate`).
2. Validates the bytes are well-formed (header + `xref` + `startxref` + `%%EOF`).
3. Prints the byte count and SHA-256 to stdout.
4. Optionally writes the bytes to a file given as argv[0].
5. Exits 0 on success; non-zero on any failure (which blocks CI).

## Why this exists

AOT-incompatibility is silent: the analyzer warnings only surface in the publish path, not the inner-loop `dotnet build`. A reflection call (e.g., `Type.GetType(string)`, `Activator.CreateInstance`, `MakeGenericType`) or a runtime codegen path (e.g., `System.Reflection.Emit`) can land in `main` without anyone noticing — until a customer tries `dotnet publish -p:PublishAot=true` and gets `IL2026` / `IL3050`.

The smoke test publishes-and-runs every commit's worth of code. Any regression that breaks AOT shows up as a publish failure or a non-zero exit code on the produced native binary.

## How to run it locally

```bash
# 1. Inner loop: build the project. The IsAotCompatible / EnableTrimAnalyzer
#    properties are on, so the build surfaces analyzer warnings even before
#    publish-AOT runs.
dotnet build tests/NetPdf.AotSmoke/NetPdf.AotSmoke.csproj -c Release

# 2. JIT run (sanity): exercise the same code path under the JIT to confirm the
#    output is what AOT should match.
dotnet run --project tests/NetPdf.AotSmoke/NetPdf.AotSmoke.csproj -c Release --no-build

# 3. AOT publish: produces a self-contained native binary. The -f net10.0 flag
#    is required because the project targets multiple frameworks.
dotnet publish tests/NetPdf.AotSmoke/NetPdf.AotSmoke.csproj \
  -c Release -f net10.0 -p:PublishAot=true -o artifacts/aot-smoke

# 4. Run the native binary. Optional second arg writes the PDF to disk.
./artifacts/aot-smoke/NetPdf.AotSmoke /tmp/aotsmoke.pdf

# 5. Compare hashes (JIT vs. AOT must match).
shasum -a 256 /tmp/aotsmoke.pdf
```

Expected stdout (on a properly-configured environment):

```
NetPdf.AotSmoke phase=1 ok byteCount=<N> sha256=<HEX>
```

## What "AOT-clean" means in this codebase

The Phase 1 surface uses none of the AOT-incompatible patterns the .NET runtime flags. Specifically, every shipped path avoids:

| Pattern | Status | Notes |
|---|---|---|
| `Type.GetType(string)` | banned | Source generators are the alternative when dynamic registration is needed. |
| `Activator.CreateInstance` (non-generic, with `Type` arg) | banned | Use `new T()` or factory delegates. |
| `Assembly.Load` / dynamic loading | banned | Static composition only. |
| `System.Reflection.Emit` | banned | No runtime codegen; source generators or pre-compiled lookup tables. |
| `MakeGenericType` / `MakeGenericMethod` | banned | Generic type constructions must be statically known. |
| LINQ `.Cast<T>()` over `IEnumerable` (non-generic) | banned in hot paths | Tight loops use `for`/`foreach` directly. |
| `Expression<T>` compilation | banned | Use delegates or source generators. |
| Unbounded reflection over user types | banned | If runtime type discovery is needed, generate a registry at compile time. |

The `NetPdf.AotSmoke` project sets:

- `IsAotCompatible = true` — turns on the AOT analyzer at build time so unsafe patterns are flagged immediately, not deferred to publish.
- `IsTrimmable = true` + `EnableTrimAnalyzer = true` — surfaces trim warnings (which often co-occur with AOT failures).
- `InvariantGlobalization = true` — drops globalization data from the published image and ensures any culture-sensitive code is forced through `CultureInfo.InvariantCulture` (which is what NetPdf uses everywhere).

## CI integration

(Phase 5.) The intended CI step is:

```yaml
- name: AOT publish + run
  run: |
    dotnet publish tests/NetPdf.AotSmoke/NetPdf.AotSmoke.csproj \
      -c Release -f net10.0 -p:PublishAot=true -o artifacts/aot-smoke
    ./artifacts/aot-smoke/NetPdf.AotSmoke /tmp/aotsmoke.pdf
    test -s /tmp/aotsmoke.pdf  # non-empty
```

This runs on `linux-x64`, `osx-arm64`, and `win-x64` in the cross-platform matrix. Each platform also feeds its produced bytes into the determinism harness's per-platform pin map (see [determinism.md](determinism.md)) so any AOT/JIT byte divergence on any platform surfaces immediately.

## Common AOT failure modes (and what to do)

- **`IL2026` / `IL2070` (RequiresUnreferencedCode)**: a method or property uses reflection in a way that won't survive trimming. Refactor to static knowledge or use a source generator.
- **`IL3050` / `IL3051` / `IL3053` (RequiresDynamicCode)**: code path requires runtime codegen. Find an AOT-friendly alternative — typically static delegates, source-generated factories, or `[DynamicallyAccessedMembers]` annotations on type parameters.
- **Native binary segfaults on startup**: usually a missing native asset (e.g., a P/Invoke library not in the publish output). Check the publish artifacts for the expected `.so` / `.dylib` / `.dll` files.
- **Native binary runs but output bytes differ from JIT**: this is a determinism regression, not an AOT regression. Investigate via `DeterminismDiagnostics.AssertByteEqualsWithDiagnostics` (run the canonical document under both JIT and AOT, compare the diff).

## Why we restrict the smoke to `PdfDocument` (not `HtmlPdf.Convert`) for now

The public `HtmlPdf.Convert(html)` path is the right surface for an end-to-end AOT smoke — but Phase 1 hasn't shipped HTML parsing / cascade / layout yet (those are Phase 2+ tasks). If we stubbed `HtmlPdf.Convert` to throw `NotImplementedException` and called it from the smoke, we'd just be testing that exception throwing is AOT-clean.

Instead, the Phase 1 smoke directly exercises the byte-writer path that *is* shipped: `PdfDocument.AddPage`, `RegisterImage` with content-hash dedup, `PlaceImage`, both `AppendContent` overloads, trailer `/ID` auto-derivation. This is where all the byte-emitting code lives. When Phase 2 lands, the smoke flips to `HtmlPdf.Convert(small-html)` and the `PdfDocument` direct call becomes redundant (and the `InternalsVisibleTo` for `NetPdf.AotSmoke` on `NetPdf.Pdf` can be removed at that point).
