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
stay unsupported. **Cycle 10 — the `@page :first` selector (in progress, branch
`phase-3-task-21-at-page-first-selector`):** selector-scoped `@page :first` rules apply on the single
(first) page, overriding the bare `@page` (margin / size / margin boxes) by cascade specificity —
`AtPageRules.EnumeratePageRules` yields bare-then-`:first` so the resolvers' last-wins cascade lets
`:first` win (a bare `!important` still beats a `:first` normal); `:left`/`:right`/`:blank`/named stay
deferred (multi-page-gated). **Cycle 11 — margin-box `border` (merged in PR #140, incl. its review):**
a declared `border` / per-side `border-<side>` strokes the box's full region — new
`BorderShorthandExpander` (margin-box bodies) → the 12 `border-*` longhands (added to
`MarginBoxStyle.CascadedStyleIds`, non-inherited) → painted by the pipeline via the shared
`FragmentPainter.PaintBorders` (extracted from the body 4-edge loop, byte-identical), over the
background band, ungated by `PrintBackgrounds`. Post-PR-#140 review: a zero-area/non-finite guard in
`FragmentPainter.PaintBorders` (a zero-height band paints nothing); a sanitized
`CSS-PROPERTY-VALUE-INVALID-001` for an un-expandable margin-box `border` marker (via `MarginBoxStyle`,
mirroring `font`); CSS-comment stripping + whole-value CSS-wide-keyword (`inherit`/…) handling in the
expander. **Cycle 12 — margin-box `padding` + the border content-inset (merged in PR #141, incl. its
review):** a declared `padding` (1–4-value box shorthand + per-side longhands) insets the box's text,
AND the cycle-11-deferred border content-inset now works. New `PaddingShorthandExpander` (mirroring
`BorderShorthandExpander`; a shared paren-aware `CssShorthandHelpers.SplitTopLevel` was extracted) → the
4 `padding-*` longhands (added to `MarginBoxStyle.CascadedStyleIds`, non-inherited) + a marker
diagnostic for an un-expandable `padding`. `PageMarginBoxPainter` insets the text by the used
border-width + padding per side via `ReadLengthPxOrZero` (§4.3 border-width gate) — placing the line at
the BORDER-box origin and shrinking the alignment extent to the content box, since
`TextPainter.CollectFragment` already adds the box's border+padding (else the inset would DOUBLE).
Post-PR-#141 review: NON-ABSOLUTE padding (a `%` or a font-/viewport-relative `1em`/`5vw`) is now
diagnosed + dropped — `MarginBoxStyle` drops any declared padding that didn't materialize to a
`LengthPx` slot (no silent zeroing) — plus vertical-axis inset regression tests. **Cycle 13 — the
`border-width`/`-style`/`-color` box shorthands (merged in PR #142, incl. its review):** the three
1–4-value border BOX shorthands distribute across the four edges. New `BorderBoxShorthandExpander`
(`border-width`/`-style`/`-color` → the per-edge `border-{side}-{width,style,color}` longhands; the
1–4-value box→edge mapping extracted into the shared `CssShorthandHelpers.ExpandBoxEdges`, which
`PaddingShorthandExpander` now also uses). The 12 longhands are already in
`MarginBoxStyle.CascadedStyleIds`, so they paint (cycle 11) + inset the text (cycle 12) with NO painter
change; a marker diagnostic surfaces an un-expandable one. The post-PR-#142 review (3 P3) cleaned stale
"deferred" docs, added cascade-order regression tests (shorthand vs longhand by importance + source
order), and broadened expander coverage (3-value color, CSS-wide across all three). **Cycle 14 — §5.3
three-box-per-edge sizing, shrink-to-fit first cut (merged in PR #143, incl. its review):** a
content-bearing edge box now shrinks to its border-box content size along the §5.3 VARIABLE axis (new
`PageMarginBoxGeometry.MarginBoxAxis` — top/bottom → width, left/right → height, corners neither), so
its background/border cover the box, not the whole band; positioned in the band by its §5.3.2.4
NAME-DERIVED role (`region.HAlign/VAlign`), with the declared `text-align`/`vertical-align` aligning
only the line WITHIN the content box (a no-op for a shrink-to-fit edge box; observable on the fixed
axis + non-shrinking corner boxes). `PageMarginBoxPainter` lays the line out first (to get the content
size), then sizes + places the box; for a box that doesn't redeclare alignment, line positions are
byte-identical to the old full-band model. Empty `content:""` / failed-font boxes keep the full band.
**Post-PR-#143 review (1 P1 + 1 P3): (P1)** box PLACEMENT was wrongly using the box's declared
`text-align`/`vertical-align` as the placement factor — `@top-center { text-align: left }` slid the
box/background to the band's left edge instead of staying centered (`@left-middle { vertical-align: top
}` slid it up) — fixed in `PageMarginBoxPainter` to place by `region.HAlign/VAlign` (the §5.3.2.4
name-derived role), the declared alignment positioning only the line within the content box; **(P3)**
tightened two cycle-reference/future-tense doc comments (`PageMarginBoxPainter` class doc +
`BorderShorthandExpander` remarks) that could read as the `border-width`/`-style`/`-color` box
shorthands being deferred (they shipped cycle 13). DEFERRED: the full §5.3 min/max-content
DISTRIBUTION (long siblings can still overlap), explicit `width`/`height`, overflow clipping.
`border-radius` + background images stay deferred. **Cycle 15 — margin-box explicit `width`/`height`
(merged in PR #144, incl. its review):** a declared `width` (top/bottom) /
`height` (left/right) sizes the box along its §5.3 VARIABLE axis, overriding shrink-to-fit (an absolute
length or a percentage of the band; `auto` shrink-to-fits, a deferred font-/viewport-relative or
`calc()` size is diagnosed + dropped). `width`/`height`
joined `MarginBoxStyle.CascadedStyleIds` (non-inherited); `PageMarginBoxPainter.TryReadExplicitSizePx`
reads them. The explicit size is content-box (box-sizing deferred); the border-box adds the
border+padding insets, and applies even to an empty `content:""` box. An explicit width can make the
content box wider than the line, so the (content-only) `text-align`/`vertical-align` is now observable
on edge boxes. Clamped to the band. **Post-PR-#144 review (1 P2 + 2 P3): (P2)** a DEFERRED
`width`/`height` (`10em` / `5vh` / `calc()`) was silently shrink-to-fitting — `MarginBoxStyle.Build` now
diagnoses (`CSS-PROPERTY-VALUE-INVALID-001`) + drops it (keyed on `resolved.IsDeferred`, mirroring the
padding policy; `auto` / `<length>` / `<percentage>` don't trip it); **(P3)** added content-box-with-insets
coverage (explicit width + padding/border → border-box; clamp-after-insets; `@left-middle { height: 50% }`
vertical percentage); **(P3)** fixed a stale `MarginBoxStyle` deferred-list doc still calling the whole
§5.3 sizing deferred. DEFERRED: the §5.3 min/max-content DISTRIBUTION for overlapping
siblings, `box-sizing`, font-/viewport-relative + `calc()` sizes, overflow clipping. **Cycle 16 — §5.3
sibling-box overlap DISTRIBUTION, first cut (in progress, branch
`phase-3-task-21-margin-box-distribution`):** boxes sharing one edge band whose desired sizes would
overlap are resolved so they don't — new `PageMarginBoxGeometry.ResolveEdgeOverlap` (center-priority: the
center box keeps its band-clamped size centered, the side boxes clamp to the side gaps; no center → two
side boxes shrink proportionally), a NO-OP when they don't overlap (the common short-content case stays
byte-identical). `PageMarginBoxPainter.Layout` refactored into two passes (compute desired rects →
`ResolveEdgeOverlaps` adjusts overlapping siblings per edge → emit). DEFERRED: the spec-strict §5.3.2
min/max-content flex (no min-content yet — it clamps rather than re-wrapping, so over-long content still
overflows), `box-sizing`, overflow clipping/wrapping. Next (Task 21
remaining, in order): the spec-strict §5.3.2 min/max-content flex + overflow clipping/wrapping / `@page
:left`/`:right`/`:blank` + named pages (multi-page-gated), then Task 22 (`string-set`/`string()` running
headers). Blocked (see `deferrals.md`):
cycle 5b bundled DejaVu Sans fallback (needs the font binary + a dependency-dossier / THIRD-PARTY-NOTICES
legal entry, CLAUDE.md #2); the multi-page driver (needs nested-container fragmentation in `BlockLayouter`).
For the live state, read the **current-state pointer at the top of [PROGRESS.md](PROGRESS.md)**
(or run `/phase-status`); `git log --oneline -1` shows the exact commit.
