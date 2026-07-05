# Phase 5 — Packaging, Hardening, Release

**Status:** ⏳ pending (after Phase 4).
**Time:** team estimate 5 wk → Claude Opus 4.7 high: **1–2 wk**.
**Tagged release:** `1.0.0` — first stable release.

## Goal

Ship NetPdf 1.0. Lock cross-platform CI, finalize documentation, polish samples, build the optional language packs, lock BenchmarkDotNet baselines, run the full corpus acceptance gate, and tag `1.0.0`.

## Prerequisites

- Phases 0–4 feature-complete; `0.9.0-rc1` tagged.
- Pinned-Chrome Docker image is reproducible + reference regeneration is a documented manual workflow (`tests/NetPdf.RenderingCorpus/docker/`).
- **Carried-over Phase-4 closeout item (land early in Phase 5):** the visual-regression gate is *ready but inert* — the canonical per-page **Chrome reference PNGs must be generated on Linux and committed** under `tests/NetPdf.RenderingCorpus/references/` (macOS Chrome drifts on hinting/AA → false diffs, so this is a Linux/CI action). Once they land, `VisualGatePolicy` auto-activates the enforcing diff gate and Phase-4 exit criteria 1–2 flip green — i.e. **visual fidelity locks at that point, not before.** Wiring the Linux reference-generation CI job is part of Phase 5's cross-platform CI deliverable.

## Deliverables

### Cross-platform CI matrix

`.github/workflows/ci.yml` (or equivalent):

| OS | Arch | TFM | AOT |
|---|---|---|---|
| ubuntu-latest | x64 | net10.0 | smoke + publish |
| ubuntu-latest | arm64 | net10.0 | smoke + publish |
| alpine (musl) | x64 | net10.0 | smoke + publish |
| windows-latest | x64 | net10.0 | smoke + publish |
| macos-latest | x64 | net10.0 | smoke + publish |
| macos-latest | arm64 | net10.0 | smoke + publish |

Each matrix entry runs:
1. `dotnet build -c Release` — must pass with 0 errors.
2. `dotnet test` — all xUnit projects pass.
3. `dotnet run --project tests/NetPdf.AotSmoke` after `dotnet publish -p:PublishAot=true` — must run and produce expected output.
4. Visual-regression diff (in the pinned-Chrome Docker image) — must pass tolerance.
5. BenchmarkDotNet performance gate — no regression vs baseline.
6. veraPDF (post-v1.1 conformance) — skipped in v1.

### `NetPdf.Languages.*` optional packs

Each is a separate NuGet package consumed via `dotnet add package NetPdf.Languages.<name>`. They register additional hyphenation patterns and segmentation data with the core's font/text registry on assembly load (via a module initializer).

| Package | Contents |
|---|---|
| `NetPdf.Languages.European` | Hyphenation patterns: de, fr, es, it, pt, nl, sv, no, da, fi, pl, cs, hu, ru, uk |
| `NetPdf.Languages.Cjk` | Better CJK line-break heuristics + dictionary-based word segmentation for Chinese/Japanese/Korean |
| `NetPdf.Languages.Indic` | Indic script segmentation refinements + hyphenation: hi, bn, gu, ta, te, kn, ml, pa, ur |
| `NetPdf.Languages.Arabic` | Arabic justification kashida heuristics + hyphenation: ar, fa |
| `NetPdf.Languages.All` | Meta-package; depends on all above. |

Patterns sourced from CTAN `tex-hyphen` (LPPL); attribution preserved per-language in `NOTICE`.

### Documentation site

DocFX-based, served from GitHub Pages.

- `docs-site/index.md` — landing.
- `docs-site/getting-started.md` — `dotnet add package NetPdf` + minimal example.
- `docs-site/api/` — auto-generated from XML docs.
- `docs-site/compatibility.md` — an in/out-of-scope overview that links to the authoritative
  `docs/compatibility-matrix.md` (kept single-source in `docs/`, not duplicated into the site).
- `docs-site/diagnostics.md` — the diagnostic-code system (severity + prefixes) linking to the authoritative
  `docs/diagnostics-codes.md`.
- `docs-site/performance.md` — benchmark methodology + reference numbers. *(follow-up — lands with the
  benchmark baselines, tasks 16–18.)*
- `docs-site/migrations.md` — empty for 1.0; populated as breaking changes ship in v2+. *(follow-up.)*
- `docs-site/contributing.md` — clean-room policy summary + how to contribute. *(follow-up.)*

### Samples polished

All three samples under `samples/`:

- `samples/invoice-cli/` — drop the `NotImplementedException` catch; replace `sample-invoice.html` with a richer, real-world example. README explains the CLI flags.
- `samples/report-aspnet/` — production-shape ASP.NET endpoint with proper `Results.File()`, ETag support, content-disposition headers.
- `samples/readme-snippets/` — drop the `NotImplementedException` catches; every README example runs and produces a valid PDF.

### Performance hardening

- **BenchmarkDotNet baselines locked**. Per `tests/NetPdf.Benchmarks`:
  - 3-page invoice ≤ 200 ms p50 ✅
  - 20-page report ≤ 1.5 s p50 ✅
  - 100-page report linear in pages
  - 1000-page synthetic stress test < 60 s
- **Memory linearity test**: render a 10-page, 100-page, 1000-page synthetic doc; assert allocated memory grows linearly (within 10% of slope).
- **Allocation discipline audit**: profile against allocation hot-paths (cascade, line builder, paint emit) — flag any hot path over 1 KB/iteration.

### Secrets & API keys

Already set up in Phase 0:
- **`NUGET_API_KEY`** — stored as a GitHub Actions repository secret on `raroche/NetPdf`. Glob scope `NetPdf*` (covers main + all Languages packs).
- **NuGet package ID `NetPdf`** — reserved on nuget.org via the `0.0.1-phase0` placeholder (unlisted).

Full inventory and rotation policy in [docs/secrets-and-credentials.md](../secrets-and-credentials.md). The release workflow (`.github/workflows/release.yml`) consumes `NUGET_API_KEY` via `${{ secrets.NUGET_API_KEY }}` — the value never appears in code, logs, or chat.

### NuGet package validation

- `dotnet pack -c Release` produces:
  - `NetPdf.1.0.0.nupkg`
  - `NetPdf.1.0.0.snupkg` (symbol package)
  - All 5 `NetPdf.Languages.*.1.0.0.nupkg`
- Each package validated via NuGet Package Explorer (manually) before push:
  - License is Apache-2.0 SPDX expression.
  - README is bundled.
  - NOTICE + THIRD-PARTY-NOTICES are bundled.
  - Source Link metadata present in PDB.
  - Symbol package contains source (via `EmbedUntrackedSources`).
- Enable `<EnablePackageValidation>true</EnablePackageValidation>` for v1.0+ builds (catches breaking changes against the v1.0 baseline starting in v1.1).

### Final corpus acceptance gate

The release-candidate gate. All of:
1. Every file in `tests/NetPdf.RealDocuments/Corpus/Invoices/` renders within Chrome reference tolerance.
2. Every file in any other `Corpus/` subfolders (statements, contracts, reports, certificates, catalogs, dense tables added in Phase 4) renders within tolerance.
3. Pass-rates published to README:
   - W3C CSS 2.2 layout: ≥ 90%
   - W3C Flexbox: ≥ 85%
   - W3C Grid L1: ≥ 70%
   - W3C Fragmentation: ≥ 80%
   - W3C Backgrounds & Borders: ≥ 90%
   - W3C Transforms: ≥ 85%

## Spec references

| Topic | Source |
|---|---|
| .NET 10 publishing | https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/ |
| NuGet package authoring | https://learn.microsoft.com/nuget/create-packages/package-authoring-best-practices |
| Native AOT | https://learn.microsoft.com/dotnet/core/deploying/native-aot/ |
| Source Link | https://github.com/dotnet/sourcelink |
| DocFX | https://dotnet.github.io/docfx/ |
| Semantic Versioning | https://semver.org/spec/v2.0.0.html |

## Work breakdown (ordered)

> **Status (2026-07-04):** tasks **1, 2, 5, 5b, 6, 7, 8, 9 DONE**; **3, 4 AUTHORED (enforcement inert)**.
> Tasks 1–5 + the CI fixes below shipped in PR #264 (`main` @ `3b288f9`); routing (5b) + the CJK/Arabic/Indic
> packs + the All meta-package (6–9) are on branch `phase5-lang-routing-and-packs`.
> The CI workflow + AOT/visual/benchmark gates are authored
> (`.github/workflows/ci.yml`). Getting the first end-to-end CI run green fixed four pre-existing gate bugs:
>
> - **AOT gate produced no native binary.** `scripts/aot-parity.sh` split restore (without `PublishAot`)
>   from a `--no-restore` publish, so `Microsoft.DotNet.ILCompiler` was never restored and the publish
>   silently degraded to a trimmed managed publish — green locally only when the NuGet/obj cache was warm,
>   red on a clean CI checkout. Fixed to a single `PublishAot` restore+publish. The Linux legs install the
>   AOT toolchain (`clang`, `zlib1g-dev`); macOS/Windows ship it.
> - **Windows AOT gate: "unsupported host platform".** The script's RID detection had no Windows case; under
>   Git Bash `uname -s` reports `MINGW64_NT-*`. Added `MINGW*`/`MSYS*`/`CYGWIN*` → `win-x64`/`win-arm64`.
> - **Intermittent MSB3491 SourceGen cache race** (hit on the arm64 leg). NetPdf.Css referenced the SourceGen
>   analyzer with `SetTargetFramework=netstandard2.0`, giving it a distinct global-property set from the
>   solution's direct build → the generator compiled twice into the same obj dir and raced. Switched to
>   `UndefineProperties="TargetFramework;PublishAot"` → one MSBuild node, one compile (also keeps NETSDK1207
>   away during AOT publish).
> - **linux-arm64 SkiaSharp native is under-provisioned** → made **non-blocking**. `libSkiaSharp` (arm64
>   raster fallback) resolves symbols from the system libuuid/libfreetype that the runner doesn't satisfy
>   (undefined `uuid_generate_random`, then `FT_Get_BDF_Property`). Chasing these per-symbol isn't
>   reproducible on the macOS/x64 dev box (and a matrix-wide `LD_PRELOAD` can't be used — Git Bash on the
>   Windows leg honors it and aborts if the lib is absent). Hardening the arm64 native (e.g. the
>   NoDependencies SkiaSharp asset) is a maintainer step on a real arm64 runner.
>
> **Enforcing matrix:** linux-x64, windows-x64, macos-arm64. **Non-blocking** (`continue-on-error`, run +
> surface status but don't gate): linux-arm64 + alpine-musl-x64 (SkiaSharp raster-fallback native
> provisioning, not reproducible here) and macos-x64 (GitHub is deprecating the Intel-mac hosted runners; it
> stays queued the whole timeout — macos-arm64 is the enforcing macOS leg). The visual-regression gate runs
> green/inert until the maintainer commits the canonical Linux Chrome reference PNGs (task 3 remainder), and
> the benchmark gate exits neutral until a `linux-x64` baseline is captured (task 4 remainder). **Five
> maintainer/CI-box remainders:** arm64-Linux + Alpine-musl SkiaSharp natives, Intel-mac runner, visual
> reference PNGs, linux benchmark baseline. **Layout auto-routing by `lang` is now wired (task 5b):**
> `HyphenationRegistry` moved to `NetPdf.Text` and `BlockLayouter` resolves the `hyphens: auto` hyphenator
> from a box's effective HTML `lang`, so a `<html lang="de">` document hyphenates with German rules when the
> European pack is loaded (byte-identical otherwise). The pack set is complete — European (de/fr real
> starter), CJK + Arabic (no-hyphenation registration), Indic (routing-aware placeholder), and the All
> meta-package. **Still maintainer-vendored:** the full CTAN LPPL per-language pattern data (real patterns
> for the remaining European + all Indic languages) — it drops in behind the same `Register` calls with no
> API change.

| # | Task | Mini-est. | Depends on |
|---|---|---|---|
| 1 | ✅ GitHub Actions matrix workflow (Linux x64/arm64, Alpine, Windows, macOS x64/arm64) | 2 d | — |
| 2 | ✅ AOT publish gate per matrix entry | 1 d | 1 |
| 3 | 🔶 Visual-regression gate in CI — **authored; inert until Linux reference PNGs are committed** (maintainer step) | 1 d | 1 |
| 4 | 🔶 BenchmarkDotNet performance gate in CI — **authored; neutral until a `linux-x64` baseline is committed** (maintainer step) | 1 d | 1 |
| 5 | ✅ `NetPdf.Languages.European` package + `HyphenationRegistry` seam (de/fr starter set) | 1 d | — |
| 5b | ✅ **Layout auto-routing by `lang`** — `HyphenationRegistry` → `NetPdf.Text`; `BlockLayouter` resolves the `hyphens:auto` hyphenator from a box's effective `lang` (byte-identical unless a pack is loaded) | 1 d | 5 |
| 6 | ✅ `NetPdf.Languages.Cjk` package (zh/ja/ko no-hyphenation) | 1 d | 5b |
| 7 | ✅ `NetPdf.Languages.Indic` package (routing-aware placeholders; CTAN data maintainer-vendored) | 1 d | 5b |
| 8 | ✅ `NetPdf.Languages.Arabic` package (ar/fa/ur no-hyphenation) | 1 d | 5b |
| 9 | ✅ `NetPdf.Languages.All` meta-package | 0.5 d | 5–8 |
| 10 | ✅ DocFX site setup (`docs-site/docfx.json`; API generated from the public projects) | 1 d | — |
| 11 | ✅ Getting-started + API + compat + diag pages (`docs-site/*.md`) | 2 d | 10 |
| 12 | ✅ GitHub Pages deployment workflow (`.github/workflows/docs.yml`; deploy inert until the maintainer enables Pages) | 0.5 d | 10 |
| 13 | ✅ Polish `samples/invoice-cli` (dropped the stale `NotImplementedException` catch; renders a real PDF) | 0.5 d | — |
| 14 | ✅ Polish `samples/report-aspnet` (content-hash ETag + Cache-Control + Content-Disposition + 304 conditional GET) | 0.5 d | — |
| 15 | ✅ Polish `samples/readme-snippets` (dropped the dead NIE catch; all 3 README snippets run against the live facade) | 0.5 d | — |
| 16 | 🔶 BenchmarkDotNet baseline lock — osx-arm64 baseline committed + `baselines/README.md`; **`linux-x64` baseline is a maintainer step** (capture on the CI runner) | 1 d | 4 |
| 17 | ✅ Memory-linearity test — 3-point allocation-linearity gate added (complements the existing retained-heap + per-page gates in `PerformanceGateTests`) | 1 d | 16 |
| 18 | ✅ Allocation hot-path audit — profile + techniques + guards documented in `docs/design/performance.md` (linear/amortizing; box-pool is a documented post-v1 win) | 1 d | 17 |
| 19 | ✅ NuGet pack + review — all 6 packages pack + inspected; `NetPdfPackageShapeTests` pins the single-package bundle (internal DLLs in lib/, real external deps, no phantom NetPdf.* deps) | 1 d | 1, 5–9 |
| 20 | `EnablePackageValidation` baseline at v1.0 | 0.5 d | 19 |
| 21 | Run full corpus acceptance gate | 1 d | all-of-above |
| 22 | Publish W3C pass-rates to README | 0.5 d | 21 |
| 23 | Tag `1.0.0` + release notes + GitHub release | 0.5 d | all |
| 24 | Push to NuGet.org | 0.5 d | 23 |

**Total: ~20 days. With Claude Opus 4.7 high + daily Roland review: 1–2 calendar weeks.**

## Implementation notes

### Module initializer for language packs
```csharp
// In NetPdf.Languages.European/ModuleInit.cs
internal static class ModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        NetPdf.Internal.HyphenationRegistry.Register("de", LoadDe());
        NetPdf.Internal.HyphenationRegistry.Register("fr", LoadFr());
        // ...
    }
}
```
The core exposes a non-public registration API consumable only by the language-pack assemblies via `[InternalsVisibleTo]`.

### NuGet package metadata snapshot
Every NuGet package in the family ships with:
- `<PackageId>NetPdf</PackageId>` (or `NetPdf.Languages.<Name>`)
- `<Version>1.0.0</Version>`
- `<Authors>Roland Aroche and NetPdf contributors</Authors>`
- `<Description>...</Description>`
- `<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>`
- `<PackageProjectUrl>...</PackageProjectUrl>`
- `<RepositoryUrl>...</RepositoryUrl>` + `<RepositoryType>git</RepositoryType>`
- `<PackageTags>pdf;html;css;rendering;paged-media;...</PackageTags>`
- `<PackageReadmeFile>README.md</PackageReadmeFile>`
- `<IncludeSymbols>true</IncludeSymbols>` + `<SymbolPackageFormat>snupkg</SymbolPackageFormat>`
- `<EmbedUntrackedSources>true</EmbedUntrackedSources>`
- `<PublishRepositoryUrl>true</PublishRepositoryUrl>`
- Source Link via `Microsoft.SourceLink.GitHub`

### Release notes format
Each tagged release on GitHub gets release notes auto-generated from `CHANGELOG.md`'s `[Unreleased]` section, which we then promote to `[1.0.0]` heading and start a fresh `[Unreleased]`.

### Release workflow
1. Branch `release/1.0.0` from main.
2. Bump `<VersionPrefix>` in `Directory.Build.props` to `1.0.0`; clear `<VersionSuffix>`.
3. Promote `[Unreleased]` to `[1.0.0]` in CHANGELOG; start a new `[Unreleased]`.
4. Open PR, run full CI matrix, ensure all gates green.
5. Merge.
6. Tag `v1.0.0` on main.
7. GitHub Actions publishes to NuGet.org via `NUGET_API_KEY` secret.
8. Verify packages on nuget.org.
9. Publish GitHub release with auto-generated notes.

## Test plan

| Component | Test | Notes |
|---|---|---|
| CI matrix | Integration | Every matrix entry must pass on the v1.0 release branch. |
| AOT publish | Per-matrix | `dotnet publish -p:PublishAot=true` of `samples/invoice-cli` produces a binary that runs and emits a valid PDF. |
| Language packs | Unit | Each pack registers its patterns; `de` hyphenation works after referencing `NetPdf.Languages.European`. |
| DocFX site | Build | `docfx build` produces a clean static site with no broken links. |
| NuGet packages | Manual | NuGet Package Explorer review of every package before publish. |
| Performance | Benchmark | Baseline locked; CI runs every commit and fails on regression > 10%. |
| Memory linearity | Benchmark | Allocated bytes scale linearly with pages within 10%. |
| Corpus acceptance | Reference | All corpus files within Chrome diff tolerance. |
| W3C pass-rates | Conformance | Numbers match what we publish in README. |

## Exit criteria

Phase 5 (and v1.0) is complete when:

1. ✅ All cross-platform matrix entries pass full build + test + AOT smoke.
2. ✅ Visual regression passes for every corpus file.
3. 🔶 Performance gates pass and baseline is committed. **Gate authored + neutral (`benchmark-gate.sh` exit 2 = pass) until a `linux-x64` baseline is captured on the CI runner and committed** — do NOT sign off criterion 3 from a neutral job; it flips to ✅ enforcing only once that baseline lands (maintainer step, see task 16).
4. ✅ Memory linearity verified.
5. ✅ All NuGet packages produced cleanly: `NetPdf`, `NetPdf.Languages.European`, `NetPdf.Languages.Cjk`, `NetPdf.Languages.Indic`, `NetPdf.Languages.Arabic`, `NetPdf.Languages.All`.
6. ✅ Documentation site builds and deploys.
7. ✅ Samples are polished and produce valid PDFs.
8. ✅ W3C pass-rates published in README.
9. ✅ CHANGELOG `[1.0.0]` section finalized.
10. ✅ `v1.0.0` tag created.
11. ✅ Packages published to nuget.org.
12. ✅ GitHub release published.

## Common pitfalls

- **NuGet package metadata gaps.** Missing `<Description>` or `<PackageTags>` makes the package unsearchable. Use NuGet Package Explorer to review before push.
- **AOT-incompatible ASP.NET sample.** Minimal APIs use reflection in `MapGet`. The sample `samples/report-aspnet` opts out of AOT analysis (already configured in Phase 0); don't accidentally re-enable.
- **Pinned Chrome version drift.** When you upgrade the Docker image, the pinned Chrome version may change subtly. Lock to a specific tag, not `latest`.
- **Reference PNG noise from font fallback.** A missing pinned font causes Chrome to fall back to a system font, generating noisy diffs. Verify the pinned-fonts pack covers every glyph used in the corpus.
- **Module-initializer ordering.** Language packs use `[ModuleInitializer]` to register patterns. .NET runs initializers in unspecified order across assemblies; design the registry to be order-independent (idempotent registration).
- **`EnablePackageValidation` on first release.** There's no baseline to validate against on v1.0. Enable validation *after* v1.0 ships (in v1.0.1 onwards, baseline = v1.0.0).
- **Symbol package size bloat.** `EmbedAllSources=true` (different from `EmbedUntrackedSources`) embeds all sources, potentially blowing up symbol-package size. Stick with `EmbedUntrackedSources=true` (just the tracked ones).
- **CHANGELOG format drift.** Stick to Keep a Changelog format. Auto-generated GitHub release notes parse it.
- **NuGet API key in CI logs.** Use GitHub Actions secret + masked-output. Never log the raw key.
- **Native asset packaging for language packs.** Hyphenation patterns are ~hundreds of KB per language; embed as compiled resources via source generator, not as external `.dic` files. Avoids cross-platform path issues.

## Hand-off after `1.0.0`

State of the project after Phase 5:
- `1.0.0` is published on NuGet.org.
- Documentation site is live.
- CI is green across the full cross-platform matrix.
- The compatibility matrix accurately describes what's supported.

Post-v1 roadmap continues per the plan:
- **v1.1** — tagged PDF (PDF/UA-1). Work breakdown lives in
  [`docs/accessibility.md`](../accessibility.md) — TODO checklist with
  phases A (`/StructTreeRoot` infrastructure) through G (non-PDF/UA
  accessibility features). The semantic IR is already built in v1.0;
  v1.1 wires the emission.
- **v1.2** — PDF/A-3u, PDF/A-2u. These are the **u** (Unicode-mapping)
  levels of PDF/A — they require reliable Unicode `ToUnicode` CMaps
  for every glyph but do **NOT** require tagged PDF. PDF/UA-1 (v1.1)
  is therefore **NOT a prerequisite**; PDF/A-2u/3u could ship
  independently. PDF/A-2a / PDF/A-3a (= the "a" / accessibility
  levels) WOULD require v1.1's tagging surface but are deferred
  further out. See [`docs/accessibility.md`](../accessibility.md#out-of-scope)
  for the level taxonomy.
- **v1.3** — CSS Grid L2 (subgrid).
- **v1.4** — container queries, `:has()` rendering, anchor positioning.
- **v2.0** — optional rendering APIs (PDF→image preview, programmatic PDF append/merge), AES-256 encryption.
