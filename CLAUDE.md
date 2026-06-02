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

Phase 5 layout→PDF production wiring in progress (interleaved with Phase 3 per Roland's
sequencing). Latest merged: cycle 5a-1 — the PDF font-registration API (PR #122, `cdc1db0`).
For the live state, read the **current-state pointer at the top of [PROGRESS.md](PROGRESS.md)**
(or run `/phase-status`); `git log --oneline -1` shows the exact commit.
