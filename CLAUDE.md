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
overflows), `box-sizing`, overflow clipping/wrapping. **Tasks 22 + 23 — running content (one PR, in
progress, branch `phase-3-task-22-23-running-content`):** a page-margin box pulls RUNNING CONTENT —
`content: string(name)` resolves a named string set by `string-set: name <content-list>` (Task 22), and
`content: element(name)` resolves the text of a `position: running(name)` element (Task 23). New
`MarginContentCollector` walks the document in document order reading raw declared values (string-set →
named string [literal + attr() content-lists]; running → element text); `CssContentList` gained
`string()`/`element()` (undefined name → empty); `BoxBuilder` skips a `position: running()` element from
the body box tree (detected from the raw value before the keyword resolver → no spurious diagnostic).
DEFERRED: cross-page "running" persistence (multi-page driver); `string-set: … content()` (AngleSharp
drops the content() function → needs a raw-CSS pre-pass like @page descriptors); `element()` renders the
running element's TEXT only (its block box deferred). **Task 21 — §5.3 min/max-content FLEX + overflow
WRAPPING (merged in PR #147, incl. its review):** overlapping sibling margin boxes whose content can WRAP are
resolved by a §5.3.2 min/max-content flex; the post-PR-#147 review keeps the CENTER box CENTERED (flexed
against an imaginary `2 × max(A, C)` box via a new `FlexPair`, sides sized in the gaps — no more tiling),
distributes a no-centre min-overflow PROPORTIONALLY to min-content, vertically centres a re-wrapped block by
its full block height, and uses the WIDEST line + the box's computed `white-space` for the re-wrap.
**Tasks 22–23 follow-up — `string-set: content()` + wrapped-line alignment (merged PR #148, incl. its
review):** `string-set: name content()` (the canonical running header) works — AngleSharp drops it, so
`CssPreprocessor`'s recovery (gated to `string-set` + a `content()` value) re-injects it into the cascade and
`CssContentList.TryParseStringSet` resolves `content()` to the element's text, GCPM-normalized as if
`white-space: normal` (NO separate pre-pass — the cascade matches selectors); and a margin box's RE-WRAPPED
lines are aligned PER LINE by the box's alignment (opt-in `BoxFragment.LineAlignFactor`; default 0 → body +
single-line margin content byte-identical). **Task 21 — `counter(page)` counter styles + `string(name,
first|last)` (merged PR #149, incl. its review):** `counter(page, <counter-style>)` formats the page
number/total in roman / alpha / leading-zero / greek via a new shared `CounterStyleFormatter` (EXTRACTED
from `BoxBuilder`'s list-marker numerals — both format identically); a non-predefined style (`hebrew`) FALLS
BACK to `decimal` (CSS Counter Styles §7.1.4 — page numbers never vanish). `string(name, first)` AND the
no-keyword DEFAULT (GCPM §7.3) pull the FIRST `string-set` assignment on the page; `string(name, last)` is
the exit value; `start` / `first-except` bail. **Task 23 — `element(name, first|last)` + GCPM-normalized
running text (merged PR #150, incl. its review):** `element(name, first)` AND the no-keyword DEFAULT (GCPM
section 7.4 — like `string()`, a conformance fix; was last-wins) pull the FIRST `position: running()` element
on the page, `element(name, last)` the exit value (`MarginContentContext.RunningElementsFirst`; the shared
`TryReadPositionedFunction` parses both `string()` + `element()`); `element()` GCPM-normalizes the running
element's text (`white-space: normal`, read BOUNDED to 64 KiB — a DoS guard). **Task 23 — `element()`
first-cut OWN-STYLE (merged PR #151, incl. its review):** a STANDALONE
`content: element(name)` paints the running element's text in the ELEMENT's own font + color —
`MarginContentCollector.CaptureOwnStyle` captures its winning font/color longhands; `PageMarginBoxPainter`
detects standalone `element()` (`CssContentList.TryGetStandaloneElement`) + builds a CONTENT `ComputedStyle`
for shaping, the box's decoration/alignment staying the box's. Post-PR-#151 review: own-style recorded in
LOCKSTEP with the text per occurrence (empty marker — no first/last style desync); paint-time line metrics
follow the content style via `BoxFragment.TextMetricsStyle` (a 32px header no longer paints at the box's 16px
pitch); inherited font/color WALKED from ancestors; own-style returned as an array (Copilot). **Task 23 —
`element()` full-block first cut: own `background` + `border` (merged PR #152, incl. its review):** a
STANDALONE `element()` also renders the running element's OWN box DECORATION — `CaptureOwnStyle` also captures
the NON-inherited `background-color` + 12 `border-*` longhands (self-only, NO ancestor walk; a normal
element's `border`/`background` shorthands are cascade-expanded); `PageMarginBoxPainter.BuildFromOwnStyle`
builds the box `style` from the element's decoration cascaded UNDER the box's own declarations (box overrides),
reusing all the box bg/border/inset machinery. Post-PR-#152 review: `currentcolor` is ORIGIN-aware (CSS Color
4 §6.2 — split into bg + border currentcolor, each resolving against the box's colour when the box declares
that decoration [it wins the cascade], else the running element's). **Task 23 — `element()` own `padding` +
`text-align` (merged in PR #153, incl. its review):** the running element's OWN
`padding-*` (self-only, added to `DecorationOwnProperties`) insets its text + grows the shrink-to-fit box
(reusing the border+padding inset machinery — no painter change), and its OWN (inherited) `text-align` (added
to `InheritedOwnProperties`, ancestor-walked) aligns its line via `ElementHorizontalAlignFactor`, the box's
own `text-align` still winning. Post-PR-#153 review: **(P2)** the inherited-property walk
(`NearestDeclaredWinner`) now RESOLVES CSS-wide keywords (CSS Cascade L5 §7) — `inherit`/`unset`/`revert`
continue to the ANCESTOR value, `initial` → `start` — so `.section { text-align: right } .rh { text-align:
inherit }` aligns the running line RIGHT (was falling back to the box's name-derived default); **(Copilot)** a
box that DECLARES `text-align` as a CSS-wide/unknown keyword keeps its NAME-DERIVED default instead of
deferring to the element (margin-box alignment isn't inherited, so `@top-center { text-align: inherit }` stays
centered). DEFERRED: `content(before|after|first-letter|marker)`, the running element's
nested BLOCK children (laid-out sub-boxes — still text-only), the box/element COINCIDING (a box property
overrides rather than nesting), RELATIVE UNITS (`%`/`em`/`calc()`) in the element's style resolve against the
page context — approximation (CSS-wide `inherit`/`initial` now resolved), a
non-absolute (`%`/`em`/`calc()`) element padding (diagnosed + dropped like the box's), vertical-edge HEIGHT
overflow, `box-sizing`, the body's own `text-align` line-positioning. **Task 23 — `element()` nested BLOCK
children (stacked lines, first cut) + vertical-edge (height) overflow (merged in PR #154, incl. its
review):** TWO `element()` increments, completing the
running element's single-box presentation. **(A) nested BLOCK children** — a running element with
BLOCK-level children (per the production `DisplayMapper` + the shared `BoxKindFacts.IsBlockLevelOuter`, UA tag
default via `HtmlDefaultDisplay`) renders each block child's text on its OWN stacked line: `MarginContent
Collector.ReadRunningElementContent` joins the per-block GCPM-normalized lines with `U+000A` (inter-block
whitespace dropped; inline runs coalesce), and `PageMarginBoxPainter` lays the content out as `pre-line` (or
`pre` for a `nowrap`/`pre` box) when a `U+000A` is present — a plain header has no `U+000A` → byte-identical
single-line `nowrap` path. **(B) vertical-edge (height) overflow** — a margin box whose content block-height
exceeds its content-box height (clamped to the band but content is TALLER) surfaces a new
`PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001` (Warning); the content still PAINTS (clipping deferred). Post-PR-#154
review: **(P1/Copilot)** the running-content read is bounded to 64 KiB TOTAL via a SINGLE budget (was N ×
64 KiB per block child); **(P2)** block boundaries stay mandatory breaks but text WITHIN each block now WRAPS
— forced-break content lays out `pre-line` + DOES min-content-flex + re-wrap (a long block child wraps under
sibling distribution / explicit width); **(P3)** the overflow diagnostic is ACTIONABLE (names the box + the
measured vs available height, once per overflowing box), and the block-child display classifier is DRY'd
through the production `DisplayMapper` + shared `BoxKindFacts.IsBlockLevelOuter` (an inline-block/-flex/-grid/
-table / `contents` / `none` / unsupported child is no longer mis-classified as block; `Box.IsBlockLevel`
delegates to the same helper). DEFERRED: the running element's real nested BLOCK LAYOUT (sub-boxes with their
own decoration / margins — still FLATTENED text per direct block child), deep recursion (each direct block
child → one line). **Overflow-clip/box-sizing cycle (in progress, branch
`phase-3-task-21-23-overflow-clip-box-sizing`) — two left-behind §5.3 deferrals, one PR:** **(A) vertical-edge
(height) overflow CLIPPING** — overflowing margin-box content no longer SPILLS: truncated at LINE granularity
(`PageMarginBoxPainter.MaxLinesThatFit` caps the painted lines to the content-box height in PASS 2;
reading-order — the first N whole lines paint, the truncated block vertical-aligned in the content box; 0 fit
→ decoration-only box); `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001` (still once per box) now also names the
KEPT/TOTAL lines. Approximations: whole-line truncation (the partial-glyph `W n` clip path is the follow-up);
the clip applies regardless of the box's `overflow` (Paged Media §6.2 default `visible` would spill — the
opt-out rides the clip-path follow-up). **(B) margin-box `box-sizing`** — an explicit `width`/`height`
honours `box-sizing` (CSS Basic UI 4 §10): `border-box` makes the declared size the BORDER box (insets come
out of the content area; floored at the insets, the content box at 0), `content-box` (initial) unchanged;
`PropertyId.BoxSizing` joined `MarginBoxStyle.CascadedStyleIds` (non-inherited, property-aware CSS-wide
handling for free), read by `IsBorderBoxSizing`; a no-op for shrink-to-fit `auto`. Merged as PR #155 incl.
its review (P2 `MaxLinesThatFit` range-narrow before the int cast; P3 stale matrix row). **Vertical-wrap /
clip-path / relative-units cycles (in progress, branch `phase-3-task-21-vedge-wrap-clip-path-relative-units`)
— the next three left-behind §5.3 deferrals, one PR:** **(A)** a VERTICAL (left/right) or CORNER box's content
WRAPS at its fixed band/corner width (was one NoWrap line spilling horizontally) — lines stack down the
variable axis, height shrink-to-fit + clamp + line clip apply; horizontal boxes keep the unconstrained
max-content measure (byte-identical). **(B)** HORIZONTAL glyph overflow (unbreakable run / clamped rigid
sibling) clips at the box's PADDING box via a PDF `q <rect> re W n … Q` clip path (new opt-in
`BoxFragment.ClipRect` → `TextPainter`; new `PdfPage.BeginRectangleClip`/`RestoreGraphicsState`), surfaced by
a width-phrased `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001`; an EXPLICIT `overflow: visible` on the box opts out
of truncation + clip + diagnostic (`MarginBoxStyle.OverflowVisible`; clip-by-default inverts the spec-initial
`visible` — documented). **(C)** font-/viewport-relative explicit `width`/`height`
(`em`/`ex`/`ch`/`rem`/`vw`/`vh`/`vmin`/`vmax`) RESOLVE via the new shared `RelativeLengthResolver` (`em` →
box font, `rem` → root, viewport → page box; `ex`/`ch` ≈ 0.5em) — kept as deferred raws by `MarginBoxStyle`;
`calc()` still diagnosed + dropped (resolved in the NEXT cycle). Merged as PR #156 incl. its review (P2
padding-box clip predicate; P2 surfaced unresolvable relative size; P3 clip doc + `PdfPageClipTests`; P3 stale
notes). **Calc / white-space / relative-padding cycles (in progress, branch
`phase-3-task-21-calc-white-space-relative-padding`) — the next three §5.3 deferrals, one PR:** **(A)**
`width`/`height`/`padding-*: calc(…)` EVALUATES via the new shared `CalcLengthEvaluator` (CSS Values 4 §10
grammar; % against the band, relative terms via `RelativeLengthResolver`, §10.5 range clamp; admitted before
the leaf resolver as a deferred raw — margin-box-scoped, BODY calc still rejects); a failed evaluation is
surfaced + falls back. **(B)** margin-box `white-space` is CASCADED + inherited (`SupportedStyleIds`) — a
declared `nowrap`/`pre` keeps a rigid single (clipped) line. **(C)** padding `%` (vs the region width, CSS
B&B §8.4) + font-/viewport-relative + calc() resolve — `ResolveUsedPaddingInPlace` rewrites USED px into the
slots so insets/TextPainter/FragmentPainter agree; element() own padding follows (em vs the BOX font —
approximation). HEIGHT flex: resolved by design (rigid at fixed width → clamp + clip). Merged as PR #157 incl. its review
(P2 case-insensitive calc units; P2 shared CalcResolver.MaxDepth/MaxBodyLength guards; P3 doc refresh; Copilot
exact-zero divisor). **Min/max-clamp / font-size / per-edge-currentcolor cycles (in progress, branch
`phase-3-task-21-calc-minmax-fontsize-border-currentcolor`) — the next three deferrals, one PR:** **(A)** the
§10.2 comparison functions `min()`/`max()`/`clamp()` evaluate, nested in calc() AND standalone
(`width: min(50%, 150px)`); `IsMathFunction` replaces `IsCalc` as the keep gate. **(B)** a root-/viewport-
relative margin-box `font-size` (`2rem`/`5vw`) resolves at paint time (`ResolveDeferredFontSizeInPlace` — page
context first, then each box before the shaper/em bases read it) — closes the 16px-fallback gap. **(C)** border
`currentcolor` is per-EDGE (CSS Color 4 §6.2): each edge falls back to its OWNER's colour (box-declared edge →
box colour, element-declared → element's) via `FragmentPainter.BorderEdgeCurrentColors` (uniform body overload
delegates, byte-identical). Merged as PR #158 incl. its review (P2 clamp(…, none) bounds; P2 math-function
font-size with parent-em bases; P3 body-length guard + cref). **Math-fns / body-calc / deep-recursion cycles (merged as PR #159 incl. its
review — 4 Copilot findings: round() |B| step normalization, a sign(NaN) crash guard, a finite-probe-classified
failure diagnostic, ContainsMathFunction scanning INTO unknown functions — plus the handoff-spotted body
explicit-width gap: `ResolveInFlowBorderBoxInlineSize` at both BlockLayouter fill sites sizes a plain
BlockContainer/ListItem with `width > 0` to width + inline borders + padding):** **(A)**
`round()`/`mod()`/`rem()`/`abs()`/`sign()` (§10.6/10.7) evaluate in `CalcLengthEvaluator`. **(B)** BODY
properties evaluate ABSOLUTE-term math functions at cascade time (`LengthResolver` → the evaluator with a NaN
context; range-aware §10.5 clamp; `CssPreprocessor` recovers the AngleSharp-dropped declarations via
`ContainsMathFunction`). **(C)** the running element's nested blocks RECURSE (one stacked line per NESTED
block, depth-capped 16, single 64 KiB budget). **Trig-exp / body-relative / element-segments cycles (merged as PR #160 incl. its review — single styled
segment keeps its own style; the recursion budget reserves the pending separator; shipped-vs-deferred wording):** **(A)** the §10.8 trig (`sin`/`cos`/`tan`/`asin`/`acos`/`atan`/`atan2`, `deg`/`grad`/`rad`/`turn` angles,
`e`/`pi`) + §10.9 exponential (`pow`/`sqrt`/`hypot`/`log`/`exp`) functions evaluate (the Term type system gained
an ANGLE kind; `hypot()` over lengths is a valid whole value). **(B)** BODY font-/viewport-relative lengths —
units AND math functions (no %) — resolve via the new post-build `DeferredLengthResolver` in-place pass
(`PdfRenderPipeline`, page box final; em per owning box, rem per root, viewport per page box; negatives for
margins/offsets); % terms stay diagnosed. **(C)** a standalone `element()`'s stacked lines render in each LEAF
block's OWN font + colour (`RunningSegment` capture lockstep with the text; one `TextRun` per segment; pitch
follows the largest segment font). **Nested-decor / border-radius / content-pseudo cycles (merged as PR #161 incl. its review — border-radius
through the production tokenizer incl. any-zero spelling; stale wording):** **(A)** a
co-declared element()'s decoration paints as a NESTED band at its content block. **(B)** a uniform absolute
margin-box `border-radius` rounds the background band (`PdfPage.FillRoundedRectangle`; strokes/body deferred).
**(C)** `content(before|after)` resolves the host's pseudo content in `string-set` — plus a REAL adapter fix
(a wholly-dropped rule now synthesizes from its recovery instead of losing it). **Segment-decor/-align/-pitch cycles (merged as PR #162 incl. its review — no double
root decoration; band Y origin includes the box insets; shipped wording):** per-line bands/alignment/pitch for
element() segments. **Body-percent / segment-margins / segment-line-height cycles (merged as PR #163 incl. its
review — percent-aware nested inline-only margin-top; zero line-height; single-pass geometry; the
AngleSharp-eats-`16PX` canary):** body %, segment margin gaps, per-segment line-height.
**Segment-padding / percent-height / auto-margins cycles (merged as PR #164 incl. its dual
review — block-once boundary padding; % height through the MEASURE pass too; float-range auto margins;
explicit `width: 0`):** vertical segment padding, definite-chain % height, §10.3.3 centering.
**Hpadding / float-percent / box-sizing cycles (in progress, branch
`phase-3-task-21-23-hpadding-float-percent-box-sizing`):** a leaf's horizontal padding insets its line's
glyphs + alignment extent (band keeps full width); float `width`/`margin-*`/`padding-*` % resolve against
the BFC content box (abspos % was already live); an explicit body width under `box-sizing: border-box` IS
the border box (floored at the insets). Next (in order): background images (gated on the body image
pipeline), per-line horizontal margins, then the multi-page driver (cross-page running +
`@page :left`/`:right`/`:blank` + named pages). Blocked (see `deferrals.md`):
cycle 5b bundled DejaVu Sans fallback (needs the font binary + a dependency-dossier / THIRD-PARTY-NOTICES
legal entry, CLAUDE.md #2); the multi-page driver (needs nested-container fragmentation in `BlockLayouter`).
For the live state, read the **current-state pointer at the top of [PROGRESS.md](PROGRESS.md)**
(or run `/phase-status`); `git log --oneline -1` shows the exact commit.
