# CLAUDE.md — NetPdf project bootstrap

Read this file first when opening this repo. It's the index. Detail lives in the docs it points to.

## What this project is

**NetPdf** — a pure C# / .NET 10 / C# 14 HTML+CSS-to-PDF rendering engine. Apache-2.0. No browser, no native Chromium, no AGPL or revenue-capped deps. Direction: Prince / WeasyPrint, not Playwright / wkhtmltopdf.

The hard problem isn't the PDF byte format — it's the HTML/CSS layout engine (block/inline/flex/grid/table + fragmentation across pages + international text shaping + the "least ugly page split" cost model). That's where the core IP lives.

## Where we are right now

The single source of truth for current status is [`PROGRESS.md`](PROGRESS.md). Read it first.

- **Repository:** `https://github.com/raroche/NetPdf` (currently **PRIVATE**; flips public at `1.0.0` launch).
- **Open-source strategy:** Develop privately through Phases 1–5. At v1.0 launch, push a clean orphan-branch initial commit to a fresh public repo + publish NuGet package. See [docs/phases/phase-5-packaging-and-release.md](docs/phases/phase-5-packaging-and-release.md).

## Start every session by running

```
/phase-status
```

That skill (under `.claude/skills/phase-status.md`) reports the active phase, build/test state, and suggested next action.

## Cross-cutting rules — apply to every change

These are non-negotiable. Every PR/commit must honor them. Detailed coding standards (SOLID, DRY-with-rule-of-three, YAGNI, naming, testing patterns, code review checklist) live in [`docs/coding-standards.md`](docs/coding-standards.md).

1. **Clean-room development.** Algorithms come from W3C / ISO / UAX specs, not from reading other implementations' source code. If you read another engine (Servo, Taffy, WeasyPrint, Chromium) for understanding, leave a one-line comment in the file noting it. Full policy in [docs/clean-room-policy.md](docs/clean-room-policy.md).
2. **Banned dependencies.** No `System.Drawing`, no browser engines, no AGPL/copyleft, no revenue-capped libraries. The `NetPdf.BannedAnalyzer` Roslyn analyzer enforces this at compile time. Approved deps: AngleSharp, AngleSharp.Css, HarfBuzzSharp, SkiaSharp (raster fallback only). Adding a new dep requires an entry in [docs/legal/dependency-dossier.md](docs/legal/dependency-dossier.md) reviewed and merged before the PackageVersion lands.
3. **AOT-clean.** No reflection in core paths. No `Activator.CreateInstance`, `Type.GetType(string)`, runtime codegen. Source generators where dynamic registration would otherwise be needed. The AOT smoke test gates CI; if it fails, the change doesn't ship. Run protocol: [docs/design/aot-smoke.md](docs/design/aot-smoke.md).
4. **Determinism.** Same input → same PDF bytes. No PRNG, no `DateTime.Now` in shipped code, deterministic compression, optional frozen `/CreationDate` via `FeatureFlags.DeterministicTimestamps`. Public contract + re-pin protocol: [docs/design/determinism.md](docs/design/determinism.md).
5. **C# 14 / .NET 10 idioms.** Use `Span<T>`, `ReadOnlySpan<T>`, `IBufferWriter<byte>`, `ArrayPool<T>`, `FrozenDictionary`, `SearchValues<char>`, `[InlineArray]`, `required`, `init`, primary constructors, file-scoped namespaces, `ref struct` enumerators. **No LINQ in hot paths** (Roslyn analyzer enforced).
6. **Apache-2.0 file headers.** Every source file starts with:
   ```
   // Copyright 2026 Roland Aroche and NetPdf contributors.
   // Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.
   ```
7. **Diagnostics, not silent corruption.** Unsupported features emit a stable code from [docs/diagnostics-codes.md](docs/diagnostics-codes.md). Never drop content silently.
8. **Performance gates** (enforced in CI from Phase 1):
   - 3-page invoice ≤ 200 ms p50.
   - 20-page report ≤ 1.5 s p50.
   - Memory grows linearly with page count.
   - **No process spawning at render time.**

## Where to look for detail

| If you need to... | Read |
|---|---|
| Know what phase / task we're on right now | [PROGRESS.md](PROGRESS.md) — single source of truth, updated as each task/phase ships. |
| Know what to build next | [docs/phases/](docs/phases/) — pick the active phase doc; follow the work breakdown. |
| Know what we **deliberately deferred** (approximation vs. throw, pickup triggers) | [docs/deferrals.md](docs/deferrals.md) — keep in sync with code when adding or picking up deferrals. |
| Know what CSS features are in/out of scope | [docs/compatibility-matrix.md](docs/compatibility-matrix.md) |
| Know the legal contract | [docs/clean-room-policy.md](docs/clean-room-policy.md) |
| Add a dependency | [docs/legal/dependency-dossier.md](docs/legal/dependency-dossier.md) |
| Emit a new diagnostic | [docs/diagnostics-codes.md](docs/diagnostics-codes.md) — and run `/add-diagnostic-code`. |
| Look up a PDF spec interpretation | [docs/pdf-spec-notes.md](docs/pdf-spec-notes.md) |
| Plan accessibility / tagged-PDF work (PDF/UA-1, ships v1.1) | [docs/accessibility.md](docs/accessibility.md) — TODO roadmap; semantic IR is built in v1, emission ships v1.1. |
| Manage secrets / API keys | [docs/secrets-and-credentials.md](docs/secrets-and-credentials.md) |
| Know how we write code (SOLID, DRY, naming, testing) | [docs/coding-standards.md](docs/coding-standards.md) |
| Run a recurring task | [.claude/skills/](.claude/skills/) — `phase-status`, `add-diagnostic-code`, `add-corpus-sample`, `add-css-property`, `render-corpus`, `bench`, `aot-check`, `uax-test`. |

## Build, test, run

```bash
dotnet build NetPdf.slnx -c Release
dotnet test NetPdf.slnx -c Release --nologo
dotnet run --project samples/invoice-cli/InvoiceCli.csproj -c Release -- input.html out.pdf
```

The sample CLI catches `NotImplementedException` and reports the Phase-0 status until Phase 1 lands. Once `HtmlPdf.Convert` is implemented, drop the catches in `samples/invoice-cli/Program.cs`.

## Project layout (high-level)

```
src/
  NetPdf/                 PUBLIC API — facade. The only project consumers see.
  NetPdf.Css/             AngleSharp.Css adapter, cascade, computed values, properties
  NetPdf.Layout/          Box gen, block/inline/flex/grid/table layouters; fragmentainer-aware
  NetPdf.Paginate/        Break resolver, cost model, bounded DP optimizer
  NetPdf.Paint/           Display list IR, gradients, shadows, Skia raster fallback bridge
  NetPdf.Pdf/             OUR PDF byte writer (objects, xref, fonts, images)
  NetPdf.Text/            HarfBuzz, bidi (UAX #9), line break (UAX #14), segmentation, fonts
  NetPdf.Svg/             SVG → display commands (Phase 4)
  NetPdf.SourceGen/       Roslyn source generators (CSS property tables, etc.)

tests/                    11 test projects covering unit, snapshots, conformance, corpus, perf, AOT, fuzz
samples/                  invoice-cli, report-aspnet, readme-snippets
docs/                     governance + phases + legal
.claude/skills/           project-scoped recurring-task skills
```

## Common pitfalls a new session should know

- **Directory has spaces and a typo** in its name (`Html to PDf Library`). Quote paths in shell commands. Don't rename — git history would get confused.
- **`.slnx` not `.sln`.** .NET 10 default; the file is `NetPdf.slnx`.
- **AngleSharp.Css is for parsing only.** Cascade, computed values, layout — all ours. Don't let AngleSharp types leak past `NetPdf.Css`.
- **Tagged PDF / PDF/UA / PDF/A are post-v1.** Build the semantic IR alongside layout (it's prepared for) but don't emit tagged structure in v1.
- **Tailwind via CDN doesn't work.** It requires JavaScript at runtime to generate the utility CSS. Two corpus samples (`02-tailwind-cdn.html`, `03-tailwind-cdn-responsive.html`) document this limitation. Pre-compile Tailwind to static CSS for it to render.
- **Phase 3 is the bottleneck.** CSS Grid track sizing, fragmentainer-aware layout, pagination cost model. Plan for 8–12 weeks. Don't underestimate.

## When in doubt

The plan file at `~/.claude/plans/i-want-to-build-parallel-eagle.md` (outside the repo) is the high-level architectural reference if you have access. The repo's `docs/phases/` are the execution detail. **If they conflict, the phase doc wins for execution detail and the plan wins for architectural decisions.**

For anything not covered above, ask Roland before guessing.

## Last verified

Phase 5 layout→PDF production wiring + Phase 3 in progress (interleaved per Roland's
sequencing). Latest: Phase 3 Task 21 — the `@page` rule. Cycle 1 (page margins, PR #130) +
cycle 2 (page size + post-PR-#131 review hardening, PR #131): a bare `@page { margin… }` /
`{ size: … }` overrides the page margins + size via `AtPageMarginResolver` / `AtPageSizeResolver`
(`src/NetPdf.Css/PagedMedia/`, sharing `AtPageRules` cascade-style applicability) →
`PdfRenderPipeline` (`MediaBox` + content area, honoring `PreferCssPageSize`). The `size`
descriptor is recovered in the pre-pass (AngleSharp drops it); `size !important` + paper-size-`@media`
ignore (§3.3) + duplicate-grammar reject + percent-margins-against-the-resolved-size all landed in
the #131 review. **Cycle 3 — page-margin boxes (content) — merged (PR #132):** the 16 §6.4 boxes
paint literal + `attr()` content — `AtPageMarginBoxResolver` → `PageMarginBoxGeometry`
(regions/alignment) → `PageMarginBoxPainter` (`CssContentList` → `InlineLayouter` → the shared
`TextPainter` pass; `!important` cascade + `none`/`normal` suppression + sanitized diagnostics +
one-font-embed landed in the #132 review). **Cycle 4 — per-box style — merged (PR #133):** a margin
box's declared `font-*`/`color` flow through the shaper + painter; declared `text-align`/
`vertical-align` override the name-derived alignment, via new `MarginBoxStyle` (per-box
`ComputedStyle` from the declared longhands; `!important` cascade + a property WHITELIST +
invalid-value diagnostics landed in the #133 review). **Cycle 5 — style inheritance — merged (PR
#134):** margin boxes inherit along the CSS Page 3 chain root element → page context (`@page`
declarations) → margin box (`MarginBoxStyle.Build` gained a `parentStyle` param); the #134 review
fixed a real bug (text-align/vertical-align are read from the box's OWN declarations, not inherited,
so the root's UA-default `text-align: start` can't override the name-derived alignment) + added
CSS-wide-keyword handling (`initial` resets, `inherit` keeps). **Cycle 6 — the `font` shorthand
(merged in PR #135, incl. its review):** `font:` works in margin-box bodies via new
`FontShorthandExpander` → `ParseRawDeclarations` (AngleSharp doesn't see margin-box bodies). The
post-PR-#135 review made expansion ATOMIC (each generated longhand validated through the production
`PropertyResolverDispatch` — any bad part rejects the whole shorthand, no partial style), required a
`<line-height>` after a `/`, stripped CSS comments quote-aware, accepted the unitless zero, and made
a system-font/malformed `font` surface a sanitized `CSS-PROPERTY-VALUE-INVALID-001` (kept as a raw
marker `MarginBoxStyle` reports) instead of silently vanishing; `oblique <angle>` / `<font-stretch>`
are pinned as a deliberate atomic-reject approximation. **Cycle 7 — parent-relative font-size/weight
in margin boxes (merged in PR #136, incl. its review):** a margin box's `em`/`%`/`larger`/`smaller`
font-size + `bolder`/`lighter` weight resolve against the inherited parent (a left-behind cycles-4–6
deferral) — the box-builder's `ResolveDeferredFontProperties` was extracted into a shared
`DeferredFontResolver` that `MarginBoxStyle.Build` also calls; `rem`/viewport font-size stays deferred
(documented gap). **Cycle 8 — margin-box `background-color` (in progress, branch
`phase-3-task-21-margin-box-background-color`):** a declared (non-inherited) `background-color` paints
a band over the box's full region behind its content — materialized via a new `CascadedStyleIds`
(kept out of the inheritance copy), resolved in `PageMarginBoxPainter` into a `MarginBoxBackgroundFill`
and filled by the pipeline (reusing `FragmentPainter`'s rect/color helpers + `PdfPage.FillRectangle`)
before the shared text pass; the post-PR-#137 review gated bands by `PrintBackgrounds`, painted
`content:""` boxes' bands, and made CSS-wide handling property-aware (`background-color: inherit`
copies the parent). **Cycle 9 — `counter(page)`/`counter(pages)` page numbers (in progress, branch
`phase-3-task-21-margin-box-counter-page`):** `content: counter(page)`/`counter(pages)` (optional
`decimal` style) resolves to the page number/total via a new `CssContentList.PageCounters` context
threaded through `PageMarginBoxPainter`; the pipeline passes `(1, 1)` for the single page (the blocked
multi-page driver supplies real numbers later). Non-page counters / `counters()` / no-page-context
stay unsupported. Next:
margin-box `border`/`padding` (border-shorthand expander + content-origin inset) / §5.3 three-box-per-edge sizing, OR `@page` selectors
(`:first`/`:left`/`:right`/`:blank`) + named pages — Roland's pick. Blocked (see `deferrals.md`):
cycle 5b bundled DejaVu Sans fallback (needs the font binary + a dependency-dossier / THIRD-PARTY-NOTICES
legal entry, CLAUDE.md #2); the multi-page driver (needs nested-container fragmentation in `BlockLayouter`).
For the live state, read the **current-state pointer at the top of [PROGRESS.md](PROGRESS.md)**
(or run `/phase-status`); `git log --oneline -1` shows the exact commit.
