# NetPdf fuzzing & security regression

This is the operational companion to the [threat model](security-hardening-plan.md) (task **SEC-2**).
Two layers verify the engine's defenses against hostile input:

| Layer | What it does | Where | Runs |
|---|---|---|---|
| **Security regression suite** | Executable assertions pinning the SSRF / LFI / XXE / DoS / PDF-output invariants | `tests/NetPdf.UnitTests/Security/SecurityHardeningTests.cs` (`Category=Security`) | Every PR (CI + `dotnet test`) |
| **Fuzz harness** | Generative — feeds arbitrary/mutated bytes to every security-critical entry point, hunting for the unknown | `tests/NetPdf.Fuzz` | Smoke on every PR; libFuzzer campaigns on demand |

The suite asserts *specific* invariants; the fuzzer looks for the ones nobody wrote a test for. **Each
subsequent `SEC-N` hardening task adds its regression tests to the suite** (a new region under the matching
taxonomy class) and, where it broadens the input surface, a seed to the fuzz corpus.

---

## Fuzz targets

`FuzzTargets` (`tests/NetPdf.Fuzz/FuzzTargets.cs`) drives five entry points. Each is a **total function**
over arbitrary bytes:

| Target | Entry point | Contract |
|---|---|---|
| `HtmlConvert` | `HtmlPdf.Convert` (under `SecurityPolicy.UntrustedHtml`, no loader, 5 s timeout) | May throw only a **sanctioned** exception (`HtmlPdfException`, `FontResolutionException`, timeout/cancellation); anything else is a finding |
| `Uri` | `UriSafetyValidator.Validate` | Must never throw on any parseable URI |
| `Font` | `FontSafetyValidator.Validate` | Must never throw on any bytes |
| `Image` | `ImageSafetyValidator.Validate` | Must never throw on any bytes |
| `Svg` | `SvgRasterizer.TryRender` | Must never throw / hang (XXE-hardened, DoS-bounded); returns `null` on failure |

The validators are held to the stricter "never throw" bar deliberately — they are the pre-decode gates that
run *before* the native libraries (HarfBuzz / Skia / the image decoders) ever see attacker bytes. A throw
there is always a defect. `HtmlPdf.Convert` is allowed a **closed set** of typed failures (`FuzzTargets.IsSanctioned`);
crucially it does **not** sanction bare `InvalidOperationException` / `NullReferenceException` — those still
count as findings, so a real bug can't hide behind an over-broad catch.

> A concrete payoff: the smoke pass immediately caught a DoS guard (deeply-nested HTML) escaping
> `HtmlPdf.Convert` as an untyped `InvalidOperationException`. It now degrades to a valid PDF plus
> `LAYOUT-RECURSION-DEPTH-EXCEEDED-001` (see the regression test in the suite).

---

## Mode 1 — smoke pass (default, CI)

Deterministic, no instrumentation required. Replays the hand-authored [seed corpus](#seed-corpus) plus a
fixed number of **reproducible** mutations per seed (a seeded LCG — a green run stays green; a finding
reproduces exactly), asserting no target throws unexpectedly or hangs.

```bash
# from the repo root
dotnet run --project tests/NetPdf.Fuzz -c Release -- --smoke                # 64 mutations/seed (default)
dotnet run --project tests/NetPdf.Fuzz -c Release -- --smoke --mutations 500 # deeper local pass
```

Exit `0` = every target survived every input; exit `1` = at least one finding (printed as
`FINDING <target>/<seed>#<mutation> [throw|hang] <detail>`). This is what the
[`fuzz-smoke`](../../.github/workflows/fuzz-smoke.yml) CI job runs on every PR, alongside the
`Category=Security` suite.

## Mode 2 — libFuzzer campaign (coverage-guided, on demand)

A real, coverage-guided campaign needs the [SharpFuzz](https://github.com/Metalnem/sharpfuzz) toolchain to
instrument the assemblies. Outline (run on Linux with `clang`/libFuzzer available):

```bash
dotnet tool install --global SharpFuzz.CommandLine
dotnet publish tests/NetPdf.Fuzz -c Release -o fuzz-out
# Instrument the assemblies under test (the facade + the four validator assemblies):
for asm in NetPdf NetPdf.Text NetPdf.Pdf NetPdf.Svg; do sharpfuzz "fuzz-out/$asm.dll"; done
# Run the campaign against the instrumented harness, seeding from the corpus:
dotnet fuzz-out/NetPdf.Fuzz.dll --libfuzzer <corpus-dir>
```

In `--libfuzzer` mode the harness routes each libFuzzer input through `FuzzTargets.RunDispatch` (the first
byte selects the target, the rest is the payload), so one campaign reaches all five targets. An unhandled
(non-sanctioned) exception is a libFuzzer crash — libFuzzer writes the reproducing input to disk.

---

## Seed corpus

`SeedCorpus` (`tests/NetPdf.Fuzz/SeedCorpus.cs`) is one place, keyed by target and taxonomy: SSRF/LFI HTML +
URIs (metadata `169.254.169.254`, private ranges, IPv4-mapped IPv6, decimal/hex/octal encodings, `file:`/`gopher:`/`dict:`),
DoS shapes (deep nesting, huge attributes, CSS `var()` bombs), font/image magic-byte + oversized-header
payloads, and XXE/entity-expansion SVG. The smoke runner replays these directly; a libFuzzer campaign uses
them as its starting seed set.

**To add a seed:** append to the matching group in `SeedCorpus.Build()` with a descriptive label. Add one
whenever a `SEC-N` task widens the input surface (e.g. wiring CSS/font fetching in SEC-3) or when a fuzz
finding is fixed (add the reproducer so it never regresses).
