# Phase 0 — Legal & Architecture Lock

**Status:** ✅ Done.
**Time:** team estimate 3 wk → actual with Claude Opus 4.7 high: ~1 day.
**Tagged release:** `0.0.1-phase0`.

## Goal

Establish the legal, architectural, and tooling foundation so every subsequent phase ships against a stable, defensible base. Freeze the public API surface. Document the clean-room development policy and the dependency review process. Prove the solution scaffolding compiles and tests run end-to-end.

## What was completed

### Governance & legal
- [LICENSE](../../LICENSE) — Apache-2.0 (chosen over MIT for explicit patent grant; the PDF spec is patent-adjacent).
- [NOTICE](../../NOTICE), [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md).
- [docs/clean-room-policy.md](../clean-room-policy.md) — the legal contract every contributor agrees to.
- [docs/legal/dependency-dossier.md](../legal/dependency-dossier.md) — written license review for every dep.
- [docs/compatibility-matrix.md](../compatibility-matrix.md) — supported / not-supported CSS features across phases.
- [docs/diagnostics-codes.md](../diagnostics-codes.md) — stable code registry.
- [docs/pdf-spec-notes.md](../pdf-spec-notes.md) — PDF Association errata and our normative interpretations.

### Build infrastructure
- [Directory.Build.props](../../Directory.Build.props) — applies to every project: `net10.0` TFM, deterministic builds, AOT-compatible flag, Source Link, snupkg symbol packages, Apache-2.0 SPDX expression.
- [Directory.Packages.props](../../Directory.Packages.props) — central package version management.
- [global.json](../../global.json) — pin .NET 10.0.203.
- `.editorconfig`, `.gitignore`.

### Solution scaffolding (`NetPdf.slnx`)
9 source projects:
- `NetPdf` (public facade)
- `NetPdf.Css`, `NetPdf.Layout`, `NetPdf.Paginate`, `NetPdf.Paint`, `NetPdf.Pdf`, `NetPdf.Text`, `NetPdf.Svg`
- `NetPdf.SourceGen` (Roslyn source generators, targets `netstandard2.0`)

11 test projects:
- `NetPdf.UnitTests`, `NetPdf.LayoutSnapshots`, `NetPdf.PaginationGolden`, `NetPdf.RenderingCorpus`, `NetPdf.W3cConformance`, `NetPdf.RealDocuments`, `NetPdf.PdfValidation`, `NetPdf.Benchmarks`, `NetPdf.TestKit`, `NetPdf.Fuzz`, `NetPdf.AotSmoke`

3 sample projects:
- `samples/invoice-cli/`, `samples/report-aspnet/`, `samples/readme-snippets/`

### Public API frozen
Files under [src/NetPdf/](../../src/NetPdf/):
- `HtmlPdf.cs` — static facade with `Convert`, `ConvertAsync` × 2, `ConvertDetailed`, `Version`.
- `HtmlPdfOptions.cs`, `PdfRenderResult.cs`, `LayoutMetrics.cs`, `TimingBreakdown.cs`, `HtmlPdfException.cs`, `FeatureFlags.cs`.
- `Diagnostics/` — `Diagnostic`, `DiagnosticSeverity`, `IDiagnosticsSink`, `SourceLocation`, `UnsupportedFeature`.
- `Resources/` — `IResourceLoader`, `ResourceKind`, `ResourceResponse`, `ResourceFailure`, `SecurityPolicy` (with `BaseUri`-sandbox default), `CachePolicy`.
- `Fonts/` — `IFontResolver`, `FontQuery`, `FontFaceData`, `FontStyle`.
- `PageSetup/` — `PageSize` (with A0–A6, Letter, Legal, Tabloid, Executive, B4, B5 statics), `PageMargins`, `CssMediaType`, `PdfVersion`.

All implementations throw `NotImplementedException("NetPdf is in Phase 0 ...")` so the API is callable but disclosed as not-yet-functional.

### Test corpus seed
Phase 1+ test corpus seeded under [tests/NetPdf.RealDocuments/Corpus/Invoices/](../../tests/NetPdf.RealDocuments/Corpus/Invoices/):
- `01-classic-pure-css.html` — flexbox, floats, `page-break-inside: avoid`, fonts, external img.
- `02-tailwind-cdn.html` — Tailwind via CDN (documents the JS-required-for-CDN limitation).
- `03-tailwind-cdn-responsive.html` — adds `@media`, `::before`, `attr()`.
- `04-anvil-running-elements.html` — **the keystone**: `position: running()`, `@page { @bottom-* { content: element(...) } }`, `counter(page)`, `<td rowspan>`, multi-stylesheet print/screen.

### Verification at end of Phase 0
- `dotnet build NetPdf.slnx -c Release` — **0 errors**, 22 warnings (all SourceLink "no git repo" — fixed by `git init`).
- `dotnet test NetPdf.slnx -c Release` — **all xUnit projects passing** (8 unit smoke tests + 5 corpus existence checks = 13/13 green).
- `dotnet run --project samples/invoice-cli/InvoiceCli.csproj -- input.html out.pdf` — runs end-to-end and gracefully reports `NotImplementedException` with the Phase-0 message.

## Key architectural decisions made in Phase 0

These are recorded in the plan's "Architectural judgments" table and govern every subsequent phase:

1. **Apache-2.0** license (explicit patent grant > MIT's silence on patents).
2. **Custom PDF byte writer** — not PDFsharp, not iText. The PDF format is the engine's foundation; we own it.
3. **AngleSharp + AngleSharp.Css** for HTML and CSS parsing. We own everything downstream (cascade, computed values, layout, paint, PDF emit).
4. **Fragmentainer-aware layout** — layouters consult the paginator *during* layout, not after. Re-layout loop bounded to 2 retries per fragmentainer.
5. **WOFF2 in v1** (Brotli is built into .NET).
6. **`SafeDefault` SecurityPolicy** = BaseUri-sandboxed file:// reads + data: URIs. HTTP(S) needs explicit opt-in.
7. **Tagged PDF / PDF/UA / PDF/A** are post-v1, but the semantic IR is built in v1 so the post-v1 work doesn't require rewriting layout/paint.
8. **Pinned containerized visual-regression environment** — fixed Chrome + fixed font pack baked into a Docker image; reference PNGs committed.
9. **Tiered language packs** — core ships UCD bidi/line-break/segmentation + English hyphenation; per-language packs (`NetPdf.Languages.*`) ship in Phase 5.
10. **Banned deps enforced by Roslyn analyzer.** No `System.Drawing`, no browser engines, no AGPL, no revenue-capped libs.

## Hand-off to Phase 1

State of the repo at end of Phase 0:
- All projects compile with 0 errors.
- All Phase-0 tests pass.
- Public API surface is the frozen contract every subsequent phase must implement against.
- Phase 1 begins by replacing `src/NetPdf.Pdf/Placeholder.cs` and `src/NetPdf.Text/Placeholder.cs` with real implementations, behind the public `HtmlPdf` facade.
