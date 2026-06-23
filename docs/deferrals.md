# Known Deferrals

Approximation contracts: features the codebase **approximates** or **rejects
loudly** today, with the conditions under which each gets picked up.

This file is **not** a status tracker for in-progress work — that lives in
[PROGRESS.md](../PROGRESS.md) (active state) and
[docs/phases/](phases/) (planned breakdown). It is also not a duplicate of
those files. Update this file when (a) you ship a cycle that adds a new
approximation/throw, or (b) you pick up a deferral and replace the
approximation with full behavior.

## Schema

Every deferral entry follows the same labels so the file is grep-able + a
parity test in
[`tests/NetPdf.UnitTests/Docs/DeferralsParityTests.cs`](../tests/NetPdf.UnitTests/Docs/DeferralsParityTests.cs)
can verify entries don't drift away from source. Required labels:

- **ID** — stable kebab-case identifier. Once published, do not rename;
  removing the entry requires removing the matching parity-test assertion in
  the same commit.
- **Status** — one of `approximated`, `throws`, `not-started`.
- **Behavior** — one line describing what the code does today.
- **Missing** — what's not implemented relative to the spec.
- **Trigger** — the condition that moves this off the deferral list.
- **Owner files** — files (and rough locations) that change when picked up.
- **Added** — PR or cycle that documented the deferral.
- **Removal condition** — explicit criterion for deleting the entry.

When you add a deferral, also add its ID to the expected list in
`DeferralsParityTests.cs` (and reference this doc in the throw-message /
approximation comment in source so a future agent can find the entry by
grepping the ID).

---

## uax-24-script-detection

- **ID** — `uax-24-script-detection`
- **Status** — `approximated`. The script-detection MECHANISM shipped; the
  script TABLE is a block-based approximation (the residual below).
- **Behavior** — `LineBuilder.Itemize` now detects a per-codepoint script and
  opens a script-change run boundary (so each script-homogeneous sub-run shapes
  with its own OpenType feature set), and `LineBuilder.Shape` shapes each run
  with `run.ScriptIso15924 ?? uniform`. The script lookup
  (`src/NetPdf.Text/Bidi/UnicodeScripts.cs`) is a sorted Unicode-BLOCK range
  table covering the ~30 major scripts, which APPROXIMATES the exact UAX #24
  Script property: a few blocks mix scripts at their edges (Coptic is carved out
  of the Greek block; others are not), and the long tail of rare scripts +
  supplementary-plane extensions (CJK Ext C–G past U+2A6DF, Arabic Extended-A/B,
  etc.) is not enumerated.
- **Missing** — an EXACT per-codepoint Script-property table generated from the
  UCD `Scripts.txt`, replacing the block approximation, so a codepoint in an
  uncovered-but-assigned range gets its own script tag instead of resolving to
  `Common` (= the surrounding / caller-uniform script). The current behavior
  feeds HarfBuzz the surrounding script tag for those codepoints (no worse than
  the pre-detection single-script approximation, but not exact).
- **Trigger** — corpus content in an uncovered assigned range (e.g. CJK Ext C+,
  Arabic Extended-A) misshapes, OR a script-boundary edge inside a mixed block
  is reported.
- **Owner files** — `src/NetPdf.Text/Bidi/UnicodeScripts.cs` (replace the block
  ranges with a generated exact table); `tests/NetPdf.UnitTests/Text/Bidi/UnicodeScriptsTests.cs`
  (lock the uncovered-range behavior + the exact table once generated).
- **Added** — Phase 3 Task 10 cycle 1 documented the original deferral; the
  detection mechanism shipped in the residual long-tail batch 3 (narrowed to the
  table-completeness residual here per PR #214 review).
- **Removal condition** — `UnicodeScripts.GetScript` returns the exact UAX #24
  Script property (UCD-derived) for every assigned codepoint, and `Shape`
  consumes it; tests cover representative uncovered-today ranges.

---

## hyphens-auto-language-routing

- **ID** — `hyphens-auto-language-routing`
- **Status** — `approximated`.
- **Behavior** — `Hyphens.Auto` always applies en-US Liang patterns
  regardless of the source run's language. Word tokenization in
  `ApplyLiangPatterns` accepts only ASCII letters [A-Za-z] (+ U+00AD soft
  hyphens per cycle 3d sub-cycle 1 Rec #6) — apostrophes split contractions
  ("don't" → "don" / "t"), and non-ASCII letter sequences (e.g., German
  umlaut, Cyrillic, accented Latin Extended) are skipped entirely.
  CSS Text L4 `hyphenate-character`, `hyphenate-limit-chars`,
  `hyphenate-limit-lines`, `hyphenate-limit-zone` are also not implemented.
- **Missing** —
  - Per-language Liang pattern routing (de-DE, fr-FR, es-ES, etc.) keyed
    off each source TextRun's BCP 47 language tag.
  - UAX #29 word-segmentation so apostrophes inside contractions don't
    truncate words + non-ASCII letter sequences participate.
  - CSS Text L4 `hyphenate-character`, `hyphenate-limit-chars`,
    `hyphenate-limit-lines`, `hyphenate-limit-zone` properties.
- **Trigger** — corpus adds non-English text needing auto-hyphenation, OR
  a user-reported case where contractions / accented letters mis-wrap.
- **Owner files** —
  - `src/NetPdf.Text/Hyphenation/EnUsHyphenation.cs` siblings — e.g.,
    `DeDeHyphenation.cs`, `FrFrHyphenation.cs`, etc., loading their
    Liang TeX pattern resources.
  - New `NetPdf.Text.Hyphenation.LanguagePackRegistry` (BCP 47 lookup →
    Hyphenator).
  - `src/NetPdf.Layout/Inline/LineBuilder.cs::ApplyLiangPatterns` —
    replace ASCII-letter check with UAX #29 word boundaries + per-language
    hyphenator selection.
  - `src/NetPdf.Layout/Inline/Hyphens.cs` XML doc — drop the "deferred"
    framing once shipped.
- **Added** — Phase 3 Task 9 cycle 3b sub-cycle 3 (en-US-only Liang +
  ASCII tokenization); cycle 3d sub-cycle 1 Rec #6 (soft-hyphen
  suppression); cycle 3d sub-cycle 4 review Finding #1 (per-position
  Liang gating, which depends on this entry's owner files for the
  tokenizer when extended).
- **Removal condition** — at least one non-English language pack ships,
  the tokenizer uses UAX #29, AND one CSS Text L4 hyphenate-* property
  is implemented.

---

## fragmentation-control-residuals

- **ID** — `fragmentation-control-residuals`
- **Status** — `approximated`. CSS Fragmentation L3 control is wired (the control PR):
  `break-before` / `break-after` (+ legacy `page-break-*` aliases) force page breaks that
  propagate through fitting ancestors (Fragmentation conformance **100%**); `break-inside:
  avoid` / `break-before:avoid` / `break-after:avoid` set the boundary `AvoidBreak` flag;
  `orphans` / `widows` are registered + drive `BreakResolver.OrphansRequired` /
  `WidowsRequired`. The residuals below don't block a criterion.
- **Behavior** — Forced page breaks (`page` / `left` / `right` + legacy `always` / `left` /
  `right`) work end-to-end. `left` / `right` currently behave like `page` (force a break)
  WITHOUT the parity refinement (inserting a blank page so the box lands on a left/right
  page). `recto` / `verso` / `all` are registered + handled by the reader AND now parsed —
  AngleSharp.Css 1.0.0-beta.144 dropped those three values before the cascade, so a
  `CssPreprocessor` value-gated recovery (`IsRectoVersoAllBreakValue`) now emits them
  verbatim so they reach the cascade + force a page break (`recto` / `verso` still lack the
  blank-page parity refinement, like `left` / `right`). The
  `*:avoid` values set `AvoidBreak`, honored by the OPTIMIZING resolver's cost; the
  production greedy `BreakResolver` is cost-insensitive, so avoid is currently inert there
  (block-flow children are already emitted atomically, so this is not visibly wrong today).
  `orphans` / `widows` flow to the resolver but have no visible effect until line-level
  paragraph splitting lands (`inline-only-block-line-splitting`); the value is read once off
  the document BODY box (PR #207 review [P2] — NOT the synthetic root, which holds the initial
  default), so per-paragraph overrides aren't honored yet.
- **Missing** — (1) left/right/recto/verso PARITY (blank-page insertion);
  (2) the production driver using the optimizing (cost-aware) resolver so `*:avoid` bites;
  (3) per-paragraph `orphans` / `widows` at line-break opportunities (needs line splitting).
  (Parsing for `recto` / `verso` / `all` SHIPPED — the `CssPreprocessor` recovery above.)
- **Trigger** — `break-before:left/right` expecting a specific page side; `break-inside:avoid`
  on a multi-page container under the greedy driver; `orphans`/`widows` once paragraphs split.
- **Owner files** — `src/NetPdf.Css/properties.json` + `KeywordResolver.cs` (registration);
  `src/NetPdf.Css/Parser/Preprocessing/CssPreprocessor.cs` (`IsRectoVersoAllBreakValue`
  recovery for the dropped values);
  `src/NetPdf.Layout/Layouters/ComputedStyleLayoutExtensions.cs` (`ForcesPageBreak*` /
  `AvoidsBreak*` / `ReadOrphans/WidowsOrDefault`); `BlockLayouter.cs` (the two break-decision
  sites — top-level loop + `EmitBlockSubtreeRecursive`); `src/NetPdf/Rendering/PdfRenderPipeline.cs`
  (orphans/widows → resolver). Parity + line-splitting are the deeper follow-ups.
- **Added** — the CSS Fragmentation control PR (registration + cascade→resolver wiring).
- **Removal condition** — parity blank-page insertion ships, the optimizing resolver drives
  production, and per-paragraph orphans/widows resolve at line-break time.

---

## inline-only-block-line-splitting

- **ID** — `inline-only-block-line-splitting`
- **Status** — `approximated` (intra-paragraph LINE splitting + orphans/widows SHIP for the common case;
  blocks with block-axis chrome or atomic inlines still force-overflow).
- **Behavior** — A SINGLE inline-only (text-bearing) block taller than a whole fragmentainer now SLICES its
  own wrapped lines across pages instead of force-overflowing: the lines that fit are emitted on the current
  page, the remainder resumes on the next via an `InlineOnlyLineSplitContinuation` (carried in
  `BlockContinuation.LayouterState` at the block's child index, mirroring the grid/multicol/table resume
  pattern). The painter walks a fragment's `InlineLayout.Lines` by array index, so a page-fragment is the
  original `Lines[]` sliced to the fitting lines + a fresh `BlockOffset` — no shaped-run buffers cross the
  page boundary (the resume page re-runs the deterministic inline pass + re-slices). CSS Fragmentation L3 §4
  orphans / widows are honored at the cut, read off the block's OWN computed value (so per-paragraph
  `widows` / `orphans` work). Block-granularity prose pagination (multi-paragraph, the common case) still
  moves whole one-line paragraphs to the next page.
- **Missing** — the slice path is gated to TEXT-ONLY, CHROME-FREE blocks (`CanSplitInlineOnlyLines`): a tall
  single block with block-axis padding/border OR inline-block / `<img>` atomics still falls back to the
  whole-block force-overflow (the atomic content-relative offsets + box-decoration-break:slice chrome
  arithmetic aren't sliced yet). Also: an intermediate slice's last line isn't justified
  (`text-align: justify` treats it as the paragraph end), and the cost-model doesn't yet weigh the split
  (`orphans` is read but the geometric fill already satisfies it; widows is enforced directly).
- **Trigger** — a SINGLE `<p>`/`<div>` with block-axis padding/border OR an inline atomic, whose text is
  taller than one whole page (rare) — it overflows the bottom of its starting page rather than splitting.
- **Owner files** — `src/NetPdf.Layout/Layouters/BlockLayouter.cs` (`EmitInlineOnlyBlockInRecursionSplitting`
  + `DispatchInlineOnlyBlock`'s split path + `ComputeInlineOnlyFitLines` / `EmitInlineOnlyBlockSlice` +
  `CanSplitInlineOnlyLines`); `src/NetPdf.Paginate/LayoutContinuation.cs`
  (`InlineOnlyLineSplitContinuation`).
- **Added** — 2026-06-20 as the line-level residual of block-granularity prose pagination; NARROWED
  2026-06-22 when text-only line splitting + orphans/widows shipped. (The recursion guards a real content
  extent via `InlineOnlyBreakMinExtentPx` so a ZERO-extent anonymous block — flex/grid content the recursion
  walks, placed past the page edge — does NOT spuriously break: the earlier flex/grid regression where both
  triggering blocks had `chunk == 0` at `start > pageBlockSize`.)
- **Removal condition** — a single inline-only block WITH block-axis chrome OR inline atomics, taller than a
  page, splits its lines across pages too (box-decoration-break:slice chrome on the first/last fragment;
  atomic placements re-indexed to the resumed lines), and an intermediate slice's last line justifies.

---

## phase-4-painter-wiring

- **ID** — `phase-4-painter-wiring`
- **Status** — `not-started` (the wiring; the IR project itself exists).
- **Behavior** — `BlockLayouter` emits `BoxFragment` records (with
  `LineFragment[]` carried on `BoxFragment.InlineLines` for inline-only
  blocks per Task 11 sub-cycle 1). `src/NetPdf.Paint/` already exists with
  the **display-list IR** types: `DisplayList`, `DisplayCommand`
  (`TextRunPayload`, `RectFillPayload`, `ImageDrawPayload`,
  `TransformPushPayload`, `OpacityPushPayload`), `TextRun`,
  `RasterImage`, `RgbaColor`, `ImageEncoding`. What's missing is the
  Phase 3 → Phase 4 **bridge** that consumes layouter output + emits
  display commands.
- **Missing** — A bridge service (e.g.,
  `NetPdf.Paint.LayoutFragmentEmitter` — final name TBD) that:
  - Consumes `IReadOnlyList<BoxFragment>` (the layouter's sink output).
  - For each fragment, emits the appropriate
    `RectFillPayload` / border / clip `DisplayCommand`s.
  - For fragments with non-null `InlineLines`, walks each
    `LineFragment.Slices` + the originating `ShapedRun` data + emits
    `TextRunPayload`-style `DisplayCommand`s positioned at successive
    baselines (line-height × index from the fragment's
    `BlockOffset`).
  - Renders the visible-hyphen glyph at line ends where
    `LineFragment.EndsWithHyphenationBreak == true`.
- **Trigger** — Phase 4 start, sequenced after Phase 3 task list
  completes — see
  [docs/phases/phase-4-visual-parity.md](phases/phase-4-visual-parity.md).
- **Owner files** —
  - `src/NetPdf.Paint/` (existing) — add the bridge type next to
    `DisplayList` + `DisplayCommand`; no new project needed.
  - `src/NetPdf.Pdf/` (existing) — `DisplayList` → PDF content-stream
    operator emission already lives here; the inline-text path needs
    per-line baseline + glyph-position math wired through.
- **Added** — Phase 3 Task 11 sub-cycle 1 documented the wiring gap
  (the layouter side of the contract shipped, painter side awaits).
- **Removal condition** — The bridge service ships + a corpus invoice
  renders text via the layouter → painter → PDF path end-to-end.

---

## inline-atomic-boxes

- **ID** — `inline-atomic-boxes`
- **Status** — `approximated` (inline `<img>` + **inline-block** SHIP first-cut — inline-atomic-boxes
  cycle; inline-flex / -grid / -table still skip + diagnose).
- **Behavior** — An inline `<img>` (`BoxKind.InlineReplacedElement`) with a resolved used size now
  participates in line layout as an ATOMIC box: `BlockLayouter.CollectInlineTextRuns` converts it into a
  one-char `U+FFFC` `TextRun` carrying an `InlineAtomic` (box + used border-box width/height); the
  glyph-centric pipeline (`TextRun → Itemize → Shape → Wrap`) reserves its advance (a synthetic
  single-glyph `ShapedRun` whose advance is the used width, shaped WITHOUT HarfBuzz — `LineBuilder.Shape`),
  the white-space preprocessor passes the atomic through verbatim (preserving the payload), the line box
  grows to fit a tall atomic (`BlockLayouter.ComputeInlineAtomicLayout` → per-line heights), and
  `BlockLayouter` emits a positioned `BoxFragment` for the box (so `ImagePainter` paints it from the image
  cache). `TextPainter` skips the synthetic glyph. **Inline-block (first cut)** — a `display: inline-block`
  box is laid out the same way: `CollectInlineTextRuns.TryBuildInlineBlockAtomic` measures its content via
  `NestedContentMeasurer` (at the available content width), computes its used border-box size (a definite
  `width`/`height` honors `box-sizing`; `auto` width shrink-to-fits to the measured content width + the
  inline chrome; `auto` height = the measured content block extent + the block chrome [block children] or
  the already-chrome-folded extent [inline-only-root]), and records the buffer so
  `EmitInlineOnlyBlockFragment` flushes the content at the atomic's content-box origin (gated by the
  `BufferingMeasureSink.ContainsDecorationOwnerFragment` two-shape rule, like the flex content-inset). The
  placed `BoxFragment` is the inline-block's BORDER box (painted by `FragmentPainter`). The OTHER atomic
  kinds (`InlineFlexContainer` / `InlineGridContainer` / `InlineTable`, and an unsized inline-replaced /
  a failed inline-block layout) still SKIP + emit `LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001` (Warning).
- **Box model (post-PR-#186 review P1)** — the img's own padding + border + margin are honored: the line
  reserves the MARGIN-box advance, the emitted fragment is the BORDER box (so `ImagePainter`, which
  subtracts the img's padding/border to recover the content box, paints correctly), and the margin-box
  bottom sits on the baseline. A plain inline `<img>` (no chrome) is byte-identical to the first cut. The
  inline-block atomic carries the same margin-box advance + border-box fragment.
- **Missing (first-cut approximations)** —
  - `vertical-align` for an inline ATOMIC honours every value: `baseline` / `top` / `bottom` / `middle` /
    `text-top` / `text-bottom` keywords, `sub` / `super` (a ±em baseline shift), and a numeric
    `<length>` / `<percentage>` (a raise off the baseline) — vertical-align completion cycle, CSS 2.2
    §10.8.1. TEXT (non-atomic) `vertical-align` honours `sub` / `super` / a numeric value (a glyph-
    baseline shift, the line box GROWS to contain it — a super run sits above same-line text — and a `%`
    uses the run's OWN line-height) AND the LINE-EDGE keywords `top` / `bottom` / `middle` / `text-top` /
    `text-bottom` (bounded first cut — position the run at the line-box top/bottom, the line middle, or the
    parent text content-area, via `InlineVerticalAlign.TextLineEdgeBaselineTopPx`). All GATED to
    inline-level runs (a block / table cell's own vertical-align doesn't shift its text — the
    reference-equality gate; non-inherited so `<sub>`/`<sup>`/`<span>` work). A line-edge run TALLER than the
    baseline-sized line now GROWS the line so it is CONTAINED (PR 3 task 7 —
    `InlineVerticalAlign.TextLineEdgeGrowth`: `text-top`/`text-bottom`/`middle` grow the max-ascent
    ascent/descent extents, `top`/`bottom` contribute a content-box floor; the painter positions the run
    within the grown line via the shared helper). Deferred for text: the parent metrics use the
    0.8/−0.2-em + 0.5-em-x-height approximation. (A declared `line-height` IS now read as the
    vertical-align `%` base — `OwnLineHeightPx` reads it via `ReadLineHeightPx`, the line-height cycle —
    so a number/length/% line-height no longer silently falls back to font-size × 1.2.)
  - An inline-block aligns by its LAST in-flow line box's baseline (CSS 2.2 §10.8.1 — it sits ON the
    surrounding text baseline; the line box is sized by the max-ascent model). The last line's descent is
    captured from the ACTUAL deepest line-bearing fragment's metrics (`BufferingMeasureSink.LastLineBox
    DescentBelowBaselinePx` — its TextMetricsStyle ?? box font + its real last-line height), so a
    nested-block inline-block with a different font-size / line-height is exact; with NO in-flow line box OR
    a computed `overflow` other than `visible` (the §10.8.1 exception) the baseline is the bottom margin
    edge (the img-ish placement). An inline SPAN overriding the font ON the last line IS now honoured (PR-3
    task 9 — `BufferingMeasureSink.DeepestLastLineRunStyle` scans the last line's slices for the deepest
    run, a strict deepen-only refinement). The baseline still uses an approximate font ascent/descent
    (0.8 / −0.2 em — the layout layer has no font-metric access; the painter uses the REAL metrics for
    glyphs, so an atomic aligns within typical-font tolerance).
  - A `text-align: justify` line carrying an inline ATOMIC now DISTRIBUTES inter-word gaps AND shifts the
    atomic right by the gaps before it (the shared `InlineJustify` helper — the painter + the inline-atomic
    placement can't disagree); `justify-all` justifies the LAST line too AND every internal forced-break
    (`<br>`)-terminated line (PR-3 task 9 — `justify-all` lifts the §7.3 forced-break exception; plain
    `justify` still leaves a `<br>` line start-aligned). (center / right / end shift the atomic — body
    text-align cycle; and the direction-relative `start`/`end` shift it to the RIGHT edge in an RTL block —
    direction pipeline, PR 2 task 5. An atomic's run-level visual order within a mixed-direction line now
    reverses together with the other per-run slices under an RTL paragraph base — the UAX #9 L2
    run-granularity reversal in `LineBuilder.Wrap` (an atomic is itself a slice); deeper-than-single-
    embedding bidi nesting is the residual approximation.)
  - An inline-block's `auto` width shrink-to-fit uses the MAX-CONTENT measured at the available width (no
    separate min-content pass); deeply nested inline-blocks recurse through `NestedContentMeasurer` (bounded
    by document depth, not a dedicated cap); LTR horizontal-tb.
  - `inline-flex` / `inline-grid` / `inline-table` atomics (which need a laid-out sub-box of a non-block
    formatting context) remain deferred.
- **Trigger** — a mixed-direction line needing bidi VISUAL reordering of an inline `<img>` / inline-block
  (the RTL text-align alignment is already honoured — PR 2 task 5), or an `inline-flex`/`-grid`/`-table`
  span. (Line-edge text `vertical-align` line growth shipped in PR 3 task 7.)
- **Owner files** —
  - `src/NetPdf.Layout/Inline/InlineAtomic.cs` — the atomic primitive (box + used width/height).
  - `src/NetPdf.Layout/Inline/{TextRun,ShapedRun}.cs` — the optional `Atomic` payload.
  - `src/NetPdf.Layout/Inline/LineBuilder.cs` — `Shape` (synthetic glyph) + the white-space
    preprocessors (atomic pass-through). Wrap treats the 1-glyph run as a non-breakable unit.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — `CollectInlineTextRuns` (convert) +
    `TryBuildInlineBlockAtomic` (inline-block layout) + `IsInlineOnlyRootContainer` (the inline-block-root
    gate), `ComputeInlineAtomicLayout` (per-line heights + placements), `EmitInlineOnlyBlockFragment` (emit
    the atomic's fragment + flush inline-block content). The remaining vertical-align / alignment /
    inline-flex/-grid/-table work extends here.
  - `src/NetPdf.Layout/Layouters/BufferingMeasureSink.cs` — `ContainsDecorationOwnerFragment` (the
    inline-only-root vs block-children two-shape flag, shared with the flex content-inset).
  - `src/NetPdf/Rendering/TextPainter.cs` — skip the atomic's synthetic glyph.
- **Added** — Phase 3 Task 11 sub-cycle 1 review Finding #4; inline `<img>` first cut shipped in the
  inline-atomic-boxes cycle; inline-block first cut shipped in the inline-block cycle.
- **Removal condition** — RTL bidi VISUAL ORDER honoured for inline atomics (the text-align alignment is
  already honoured — PR 2 task 5; line-edge text line growth shipped — PR 3 task 7; last-line per-RUN
  deepest-font metrics + justify-all on internal `<br>` shipped — PR 3 task 9), the inline-block's last-line
  baseline min-content shrink-to-fit, and inline-flex / -grid / -table atomics laid out (no longer
  `LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001`).

---

## table-auto-fixed-spans-borders

- **ID** — `table-auto-fixed-spans-borders`
- **Status** — `approximated`. Phase 3 Task 14 cycle 2 hardening
  Finding #1 lifted the depth==1-only continuation propagation
  limit for nested tables — tables at any in-flow recursion depth
  now split cleanly across pages (the `NoBreakBreakResolver`
  depth≥2 atomic fallback was deleted in this hardening; the
  TableLayouter's single-oversized-row forward-progress fallback
  remains the safety net).
- **Behavior** — `TableLayouter` (Phase 3 Task 12 sub-cycles 1 + 2)
  walks the Table → TableGrid → row → cell hierarchy via a two-phase
  protocol — a pre-measure pass populates per-row cell content
  heights (via nested `BlockLayouter`s buffering into per-cell
  `MeasuringFragmentSink`s) so the wrapper's border-box block extent
  reflects the measured table content (preventing siblings from
  overlapping when CSS `height: auto`); then an emit pass walks the
  rows + emits row → cell → cell-content fragments in paint-safe
  order (cell backgrounds / borders paint UNDER text glyphs). Each
  nested cell-content `BlockLayouter` runs against a FRESH
  `BreakResolver` scoped to that cell, isolating the outer table's
  checkpoint state from cell-internal pagination. Columns split
  equally across the wrapper's content inline-size (column count =
  max occupied column index + 1 from the cell-placement grid).
  Spans (`colspan` / `rowspan`) work via the 2D occupancy-grid
  algorithm (CSS Tables L3 §3 + HTML5 forming-a-table) — each row
  walks a column cursor left-to-right, skipping slots occupied by
  rowspan continuations from earlier rows, then anchors the cell at
  the cursor with its `rowspan × colspan` slot rectangle marked
  occupied; spanning cells receive `colspan × columnWidth` inline +
  sum of covered rowHeights block; row heights start as
  `max(content extent)` over `rowspan=1` cells and a second pass
  (ascending rowspan) lands any excess from `rowspan>1` cells on
  the LAST row of the span. The CSS Tables L3 §11 spec-strict
  distribution-proportional algorithm is sub-cycle 5+ work. No
  `border-collapse`, no RTL
  flips. **Task 13 cycle 1 — multi-page row splitting at row
  boundaries.** When the row stack exceeds the fragmentainer
  block-size, the table now consults the break resolver before
  each row + returns `PageComplete(TableContinuation)` for the
  first row that doesn't fit; the dispatching `BlockLayouter`
  stashes the `TableContinuation` in `BlockContinuation.LayouterState`
  + the next-page `BlockLayouter` re-constructs a `TableLayouter`
  with the captured continuation to emit the remaining rows.
  Captions emit only on their respective edge page (top on
  page 1; bottom on the last page). A single oversized row taller
  than the fragmentainer falls back to forced-overflow forward
  progress + emits the `PAGINATION-FORCED-OVERFLOW-001`
  diagnostic. **Phase 3 Task 14 cycle 2 hardening Finding #1 lift**
  — the depth==1-only nested-table continuation propagation limit
  has been removed. Tables at ANY in-flow recursion depth
  (including the canonical `<html><body><table>` shape from real
  HTML) now split across pages cleanly via the chain-of-
  `BlockContinuation` return contract from `EmitBlockSubtreeRecursive`;
  the `NoBreakBreakResolver` depth≥2 atomic fallback was deleted
  in the same hardening pass. **Task 13 cycle 2 —
  `<thead>` / `<tfoot>` per-page repeat.** Header rows
  (collected from `<thead>` / `display: table-header-group`)
  repeat at the TOP of each page the table spans; footer rows
  (from `<tfoot>` / `display: table-footer-group`) repeat
  IMMEDIATELY AFTER the last body row that fits on each page
  (CSS Tables L3 §3.6 / §11). `CollectRows` now classifies each
  collected row by group kind (`Header`, `Body`, `Footer`) and
  reorders so headers come first + footers last regardless of
  HTML5 source order (HTML5 permits `<tfoot>` before `<tbody>`;
  the spec says it still renders at the end). The body-row
  pagination loop reserves the footer-stack height in
  `fragmentainer.UsedBlockSize` BEFORE walking body rows so the
  resolver's RemainingBlockSize budget already excludes the
  footer reservation; the cycle-1 paint-safe row → cell →
  content emit order continues to hold within each section.
  `TableContinuation.RepeatHead` / `RepeatFoot` flags drive the
  resume page: when set, the resume layouter re-emits the
  header at the top + footer at the bottom of the body window.
  The new `LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001` diagnostic
  fires when header + footer combined exceed the fragmentainer
  (no room for any body row alongside the repeat contract) —
  header + footer commit atomically + body is skipped to avoid
  infinite continuation loops. The locked cycle-2 footer
  position is IMMEDIATELY AFTER THE LAST BODY ROW on each page
  (not bottom-anchored to the fragmentainer); sub-cycle 3+ may
  revisit bottom-anchoring. **Sub-cycle 3 — captions (`<caption>`) lay
  out as block fragments above (`caption-side: top`, default) or
  below (`caption-side: bottom`) the table grid; caption inline-
  size = table wrapper's content-inline-size; the writing-mode-
  relative `block-start` / `block-end` keywords map to `top` /
  `bottom` for LTR horizontal writing modes only (RTL + vertical-
  axis writing modes deferred to a future sub-cycle alongside the
  rest of the writing-mode work). The sub-cycle 1 + 2
  `LAYOUT-TABLE-FEATURE-UNSUPPORTED-001` diagnostic for captions
  is gone.** **Sub-cycle 4 — when `table-layout: fixed` is set,
  column widths derive from `<col>` / `<colgroup>` `width` (Pass A),
  first-row cell widths (Pass B), then equal-distribute the
  remaining inline-size to columns with no declared width
  (Pass C). Sub-cycle 4 hardening
  Finding 1 added Pass D reconciliation: when the column sum is
  below the wrapper's content-inline-size, leftover space is
  distributed equally across ALL columns (CSS 2.1 §17.5.2.1); when
  the column sum exceeds the wrapper, declared widths are kept and
  the table grid overflows the wrapper in the inline axis (row +
  caption fragments grow to the column sum so author intent is
  preserved) + `LAYOUT-TABLE-INLINE-OVERFLOW-001` is emitted with
  the column sum + wrapper content-inline-size in the message.
  Sub-cycle 4 hardening Finding 2 fixed first-row colspan
  partial-declare semantics: when some spanned columns are pre-
  declared by Pass A, the cell's declared width minus the sum of
  already-declared columns is distributed across the remaining
  undeclared columns (spec-correct; pre-fix divided the full cell
  width by the full colspan regardless). Sub-cycle 4 hardening
  Finding 3 attempted to make CSS `width` cascade-aware (per CSS 2.1
  §17.5 the HTML `width` attribute is a low-specificity presentational
  hint that should lose to explicit author CSS), but the current
  cascade pipeline (`BoxBuilder.ApplyDefaults`) eagerly populates every
  ComputedStyle slot with the property's initial value, collapsing
  the distinction between "author wrote `width: auto`" and "no author
  rule, defaulted to auto". Sub-cycle 4 hardening therefore keeps the
  pre-fix behavior (HTML `width` attribute wins when CSS resolves to
  0) + documents the limitation inline in `ReadColumnWidthPx`. **NetPdf's
  fixed-layout approximation: CSS 2.1 strictly requires a definite
  table width for `table-layout: fixed`. NetPdf currently treats the
  wrapper's resolved content-inline-size (from CSS `width` or the
  containing-block width) as the effective table width, even when
  `table.width` is `auto`. Sub-cycle 5+ may revise once `width: auto`
  shrink-to-fit lands.** **Sub-cycle 5 — `table-layout: auto`
  (default) now runs the CSS Tables L3 §3 shrink-to-fit algorithm.
  Per-cell min-content + max-content widths are measured via
  speculative cell-content layouts at `cellInlineSize = 1.0`
  (min-content — force-wrap at every UAX #14 break opportunity)
  and `cellInlineSize = 1e6` (max-content — no wrap pressure);
  the buffered fragments + diagnostic sinks from the speculative
  passes are discarded. Per-column min/max aggregated across all
  cells anchored at that column (colspan=1 first; colspan>1 then
  distributes any excess equally across the spanned columns —
  symmetric to fixed-layout Pass B's partial-declare semantics).
  Table effective width = `clamp(contentInlineSize, sum(colMin),
  sum(colMax))`. Distribution has three branches:
  (a) **overflow** when `sumMin > contentInlineSize` — every
  column gets its colMin; row + caption fragments grow to the
  column sum; `LAYOUT-TABLE-INLINE-OVERFLOW-001` is emitted
  (mirrors the fixed-layout Pass D contract);
  (b) **saturated** when `contentInlineSize >= sumMax` — every
  column gets its colMax + the extra space is distributed equally
  across all columns;
  (c) **interpolation** otherwise — linear interpolation:
  `widths[c] = colMin[c] + (tableWidth - sumMin) *
  (colMax[c] - colMin[c]) / (sumMax - sumMin)`. The CSS Tables L3 §3
  proportional-weight distribution is a deterministic linear-
  interpolation approximation. The min/max signal comes from
  `MeasuringFragmentSink.MaxInlineExtentFromCellOrigin`, which
  prefers `InlineLayout.Lines[i].TotalAdvance` (actual shaped-text
  width) when available + falls back to the buffered fragment's
  border-box width otherwise. Cells with no inline-only-block
  fragment (block-level content without text) participate via the
  border-box fallback but don't differentiate min vs max. Empty
  cells contribute `min = max = 0`; clamp enforces `colMax >= colMin`.
  Performance: per-cell 2× speculative measurement is unbounded by a
  budget today — sub-cycle 6+ may cache or short-circuit when
  min == max trivially.**
  Tables that overflow the page emit
  `PAGINATION-FORCED-OVERFLOW-001`; a Table wrapper with no
  TableGrid child (malformed box tree) emits
  `LAYOUT-TABLE-FEATURE-UNSUPPORTED-001` (NOT a pagination overflow
  code — the anomaly is structural).
- **Missing** — Per CSS Tables L3 + HTML5 §4.9.11: percentage
  column widths; full grid/table used-inline-size reconciliation for
  content-shrink scenarios; §6.3 border-collapse + border-spacing;
  §6.4 column-group widths beyond Pass A fallback;
  §11 spec-strict rowspan distribution-proportional algorithm;
  CSS Tables L3 §3 spec-strict proportional-weight column-width
  distribution (sub-cycle 5 ships a deterministic linear-interpolation
  approximation + a deterministic equal-split colspan distribution);
  block-level fixed `width` honoring in cell content + replaced-
  element intrinsic-width measurement (sub-cycle 5's measurement
  reads inline-only-block line widths only; block-level cell
  content falls back to the border-box = available width, so
  fixed-width block content doesn't differentiate min vs max);
  cell intrinsic-width caching to amortize the 2× speculative-
  measurement cost across re-layout passes;
  HTML5 colspan='0'/rowspan='0' remainder semantics; RTL writing
  modes / row reversal / caption inline-axis keyword routing; HTML
  width attribute cascade precedence (the HTML `width` attribute
  should ideally be a low-specificity presentational hint in the
  cascade, not a layout-time fallback consumed AFTER computed
  values resolve — but the current cascade pipeline
  (`BoxBuilder.ApplyDefaults` → `PropertyResolverDispatch.Resolve`)
  eagerly fills every `ComputedStyle` slot with the property's
  initial value, collapsing the distinction between "author wrote
  `width: auto`" and "no author rule, defaulted to auto" — both
  report `IsSet(PropertyId.Width) = true` with a `Keyword(auto)`
  slot. Sub-cycle 4 hardening Finding 3 was a documentation-only
  pass; the layout-time fallback path (read CSS `width`, fall back
  to the HTML `width` attribute when CSS resolved to 0) kept its
  pre-fix behavior because cascade-aware gating was infeasible
  given the `ApplyDefaults` constraint. An explicit-author-rule
  bitmap or side declaration table consulted PRE-defaults is the
  spec-correct fix, deferred to sub-cycle 6+).
- **Trigger** — corpus invoice needs proper column widths
  (typical), OR a user-reported case where a table renders with
  equal columns when it shouldn't.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/TableLayouter.cs` — sub-cycle 5
    shipped the CSS Tables L3 §3 auto-table-layout shrink-to-fit
    algorithm via per-cell min/max content speculative measurement +
    linear-interpolation distribution. Task 13 cycle 1 added multi-
    page row splitting via resolver-driven row-level pagination +
    `TableContinuation` resume contract (outer-loop integrated;
    nested-recursion path used the `NoBreakBreakResolver` to keep
    nested tables atomic). Phase 3 Task 14 cycle 2 hardening
    Finding #1 lifted that limit by refactoring
    `EmitBlockSubtreeRecursive` to return a `LayoutContinuation?`
    chain that propagates nested table/multicol breaks through any
    in-flow recursion depth; `NoBreakBreakResolver`'s depth≥2
    fallback was deleted (the class remains, still used for
    captions). The `TableLayouter` single-oversized-row forward-
    progress fallback is the safety net.
    Phase 3 Task 17 cycle 5c.2d added **intra-cell row splitting at
    BLOCK granularity**: a single body row whose cell content stacks
    block children taller than the page now breaks WITHIN itself
    across pages (`TableContinuation.RowSplitOffset` carries the
    cell-relative cut; the resume page re-measures + re-slices
    deterministically) instead of force-overflowing. Cell content is
    measured at full natural height (`SuppressBlockPagination` on the
    cell fragmentainer — pagination is the table's job, not the
    cell's). The dry-run wrapper-sizing (`DryRunCommittedBlockSize`)
    is split-aware so the outer dispatch propagates the continuation.
    Tight cycle-1 scope: a split row OWNS each of its pages (the next
    row starts fresh after the tail); enabled only when the table has
    no footers + no bottom captions + the row carries no rowspan
    origin. A single ATOMIC block taller than the page (explicit
    `height`, no inner break opportunity) still force-overflows —
    shares the `inline-only-block-line-splitting` line-granularity
    deferral.
    Remaining: spec-strict §11 rowspan distribution-proportional
    algorithm; §6.3 border-collapse model + `border-spacing`;
    intra-cell splitting for rows WITH footers / bottom captions /
    rowspan origins, and packing a following row below a split row's
    tail (currently the next row starts fresh); row-level
    `break-inside: avoid`; RTL writing modes / row reversal / caption
    inline-axis keyword routing; HTML5 colspan='0'/rowspan='0'
    remainder semantics; percentage column widths.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs::PreMeasureTableIfNeeded`
    — sub-cycle 5 hardening Finding 6 now consumes the table's
    `MeasuredUsedInlineSize` to widen the wrapper's border-box
    inline extent when the grid overflows. Both outer-AttemptLayout
    + nested-recursion paths apply the widening.
- **Added** — Phase 3 Task 12 sub-cycle 1; sub-cycle 2 added
  `colspan` / `rowspan` cell merging; sub-cycle 3 added caption
  layout (`caption-side: top` / `bottom`); sub-cycle 4 added the
  `table-layout: fixed` algorithm (`<col>` / `<colgroup>` + first-
  row cell widths drive per-column widths); sub-cycle 4 hardening
  added Pass D reconciliation + first-row colspan partial-declare
  semantics; sub-cycle 5 added the CSS Tables L3 §3 auto-table-
  layout shrink-to-fit algorithm (per-cell min/max content via
  speculative measurement + linear-interpolation distribution +
  overflow / saturated / interpolation branches + shared
  `LAYOUT-TABLE-INLINE-OVERFLOW-001` diagnostic with the fixed-
  layout path); sub-cycle 5 hardening added: (Finding 1) BoxBuilder
  wraps inline-only TableCell children in `AnonymousBlock` so the
  cell's direct text contributes to intrinsic widths; (Finding 2)
  auto-layout incorporates `<col>` / `<colgroup>` / first-row cell
  widths as per-column min/max floors; (Finding 3) cell padding +
  border contribute to intrinsic widths + inner content fragments
  are offset by the cell's inner content-box origin; (Finding 4)
  per-table intrinsic-measurement budget + `LAYOUT-TABLE-INTRINSIC-
  MEASUREMENT-BUDGET-EXCEEDED-001` diagnostic; (Finding 5) new
  `OverflowWrap.BreakWord` enum variant + intrinsicSizingMode flag
  so the min-content speculative pass honors CSS Text L3 §5.1's
  carve-out (break-word's soft opportunities don't count for min-
  content); (Finding 6) caption inline-size matches the grid's
  used inline-size + wrapper widens when the grid overflows.
  Phase 3 Task 13 cycle 1 added multi-page row splitting at row
  boundaries: the table consults the break resolver before each
  row + returns `PageComplete(TableContinuation(NextRowIndex))`
  when the next row would overflow; the dispatching
  `BlockLayouter` stashes the `TableContinuation` in
  `BlockContinuation.LayouterState` so the resume page can
  re-construct a fresh `TableLayouter` with the carried
  continuation. Top captions emit only on the first page; bottom
  captions only on the last. Nested-recursion tables stay atomic
  via the new `NoBreakBreakResolver` (cycle 2+ deferral).
  Phase 3 Task 13 cycle 2 added `<thead>` / `<tfoot>` per-page
  repeat: `CollectRows` now classifies rows by group kind +
  reorders so headers come first + footers last regardless of
  HTML5 source order; body-row pagination reserves footer-stack
  height before walking body rows so resolver budgets exclude
  the footer; `TableContinuation.RepeatHead` / `RepeatFoot` flags
  drive the resume page's re-emit. New
  `LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001` diagnostic for the
  catastrophic header+footer-exceeds-fragmentainer case. Locked
  footer position: immediately after the last committed body
  row (not bottom-anchored).
- **Removal condition** — All "Missing" items above are
  implemented: percentage column widths; §6.3 border-collapse +
  border-spacing; multi-fragmentainer
  row splitting; §11 spec-strict rowspan distribution-proportional
  algorithm; CSS Tables L3 §3 spec-strict proportional-weight column
  distribution; block-level fixed `width` honoring + replaced-element
  intrinsic-width measurement; HTML5 colspan='0'/rowspan='0'
  remainder semantics; RTL writing modes; HTML width attribute as a
  low-specificity presentational cascade hint.

---

## multicol-balancing-pagination

- **ID** — `multicol-balancing-pagination`
- **Status** — `approximated` (cycles 1-4 + post-PR-#59 + post-PR-#60
  review hardening ship fixed-column-count + column-width-derived
  auto count (absolute resolved lengths only — font-relative values
  deferred to sub-cycle 5+) + equal-split + multi-page splitting
  through any recursion depth + `column-fill: balance` /
  `balance-all` with correct last-fragment semantics + a real fit-
  search instead of the average-height heuristic; column rules +
  `column-span: all` + the balance-result perf cache + font-relative
  `column-width` resolution remain sub-cycle 2+ scope). Phase 3
  Task 14 cycle 2 hardening Finding #1 lifted the depth==1-only
  continuation propagation limit — nested multicols at any in-flow
  recursion depth now split cleanly across pages. Cycle 3 +
  post-PR-#59 review hardening ship the correct column-balancing
  algorithm: a binary-search fit-probe finds the smallest column-
  block-size where all content fits in N columns, with correct
  `balance` vs `balance-all` semantics, resume-aware pre-measure,
  multi-window pre-measure for long content, and margin-aware
  extent capture. Cycle 4 ships the CSS Multi-column L1 §3.3 used-
  column-count derivation for absolute resolved lengths:
  `column-width: <Npx>` alone or combined with `column-count`
  derives N from the container's content inline-size + column-gap,
  with single-column degenerate fallthrough when derivedN == 1
  (e.g., `column-width` larger than the container). Post-PR-#60
  review hardening: (F#1) admits `column-width: 0` per spec §3.1's
  used-value 1px floor; (F#3) `column-count: 1` now reaches the
  MulticolLayouter per spec §1's BFC contract (degrades to
  single-column fallthrough); (F#4) the int-cast in the derivation
  helper is now clamped to int.MaxValue BEFORE the cast so huge
  finite ratios don't trigger undefined behavior; (F#5) the
  single-column fallthrough now shares its resume decode +
  PageComplete packaging + diagnostic emission with the multi-
  column path.
- **Behavior** — `MulticolLayouter` (Phase 3 Task 14 cycles 1-2)
  recognizes a block container with `column-count: N` (integer ≥ 2)
  as a multicol container. Detection is via
  `ComputedStyle.ReadColumnCount()` from BoxBuilder-produced
  `BlockContainer` boxes; there is no dedicated `BoxKind.Multicol*` —
  multicol is a layout-time concept layered on top of regular block
  containers, mirroring how CSS encodes it as a property on the
  block-level box. `BlockLayouter` dispatches into
  `MulticolLayouter` for such children; the layouter computes the
  per-column inline size as
  `(containerContentInlineSize - (N-1) × columnGap) / N` (columnGap
  defaults to 16 px when not declared — `normal` per CSS Multi-column
  L1 §6.1 resolves to 1em, hard-coded for cycle 1), constructs a
  sub-fragmentainer per column with `contentInlineSize =
  perColumnInlineSize` + `blockSize = containerContentBlockSize`,
  and runs a nested `BlockLayouter` per column. When the nested
  layouter returns `PageComplete(BlockContinuation)` (column
  overflow), the continuation is fed into the NEXT column's nested
  `BlockLayouter` as `incomingContinuation` so emission resumes at
  the deferred child. The outer multicol box emits ONE `BoxFragment`
  sized to the container's border-box; per-column content fragments
  land INSIDE it via a `ColumnFragmentSink` decorator that
  translates each emitted `InlineOffset` by `columnIndex × (per-
  ColumnInlineSize + columnGap)`.
- **Cycle 2 multi-page multicol** — when content overflows N
  columns on the current page, `MulticolLayouter` packages the
  LAST-column's overflowing `BlockContinuation` into a
  `MulticolContinuation(NextChildIndex, ConsumedBlockSize,
  PerChildLayouterState)` + returns `PageComplete`. The dispatching
  `BlockLayouter` wraps that in
  `BlockContinuation(ResumeAtChild=multicolChildIdx,
  LayouterState=MulticolContinuation)` so the next-page
  `BlockLayouter` invocation re-dispatches the multicol with the
  carried continuation. The resume page's `MulticolLayouter`
  unpacks `PerChildLayouterState` as the FIRST column's nested
  `incomingContinuation` — content resumes at the exact child the
  prior page deferred at (mirrors Task 13 cycle 1's row-pagination
  pattern for tables). `LAYOUT-MULTICOL-FORCED-OVERFLOW-001` is
  SUPPRESSED for clean multi-page splits; per cycle 2 hardening
  Finding #1, ANY in-flow recursion depth now propagates cleanly —
  the diagnostic now fires only on resume pages that can't make
  forward progress (single-oversized-child fallback). Floats
  containing multicol content are still atomic (their out-of-flow
  continuation propagation is deferred under
  `float-continuation-propagation`).
- **Cycle 3 + post-PR-#59 review hardening column balancing** —
  when computed `column-fill` is `balance` (the spec default) or
  `balance-all` AND the multicol container has `height: auto` AND
  `column-count` ≥ 2, `MulticolLayouter` runs a binary-search fit-
  probe pipeline:
  - **Resume-aware pre-measure (F#3 + F#4 + F#5).** The
    `_incomingContinuation` is decoded BEFORE pre-measure so resume
    pages start from the right child / nested state. The pre-measure
    LOOPS over `BlockContinuation` results — each iteration uses a
    `perColumnBlockSize × columnCount × 2` window + adds the
    fragmentainer's `UsedBlockSize` (margin-aware cursor extent
    including trailing margins + collapsed-margin effects) to the
    accumulator until `AllDone` or the iteration cap
    (`MaxPreMeasureIterations = 8`).
  - **Last-fragment detection (F#2).** `balance` balances only the
    LAST fragment; non-final fragments use serial fill.
    `balance-all` balances every fragment. Detection: if
    `totalSerialExtent ≤ perColumnBlockSize × columnCount`, content
    fits in N columns → this IS the last fragment.
  - **Binary-search fit-probe (F#1).** When balancing fires, a
    binary search over `[ceil(total/N), perColumnBlockSize]` at 1px
    resolution finds the smallest column-block-size where
    `FitsInNColumns(...)` returns true (= a serial column-fill
    simulation reaches `AllDone` in ≤ N columns). The pre-fix
    `ceil(total/N)` heuristic was wrong for indivisible content:
    3 × 80px in 2 columns produced ideal=120 → fits 1 per column →
    3rd child spilled to page 2 even though 160px columns fit all
    3 on one page.
  - **`ConsumedBlockSize` accumulator (F#8).** Multi-page
    `MulticolContinuation` uses `effectiveColumnBlockSize` (= the
    balanced height, or `perColumnBlockSize` when balancing is
    inactive) instead of always using `perColumnBlockSize`. Pre-fix
    the accumulator over-counted by `perColumnBlockSize -
    effectiveColumnBlockSize` per page when balancing fired.
  - **`IsHeightAuto` predicate (F#7).** Correctly classifies
    `height: 50%` (Percentage) and `height: calc(...)` (Calc) as
    NON-auto. Pre-fix `slot.Tag != LengthPx` incorrectly treated
    percentage heights as auto, routing them into the balancing
    path + over-shrinking the columns.
  - Containers with `height: auto` AND `column-fill: auto` keep the
    cycle 1+2 serial-fill behavior; containers with an explicit
    `height` (LengthPx or Percentage) also keep the serial path
    (conservative — matches Prince / WeasyPrint, avoids over-shrink
    drop-out).
  - **Cost.** When balancing is active: pre-measure ≈ 1×, fit-search
    ≈ O(log range) × `columnCount` `BlockLayouter` dry-runs, layout
    1×. Worst case ~12 dry-runs total for a 1000px range with N=2.
    The post-PR-#59 deferred F#6 perf-cache would memoize the fit-
    search result per Box; sub-cycle 2+ scope.
- **Missing** —
  - **Font-relative `column-width`** (CSS Multi-column L1 §3.1):
    cycle 4 reads only resolved `LengthPx` slots; font-relative
    values (`em`, `rem`) AND percentages are returned as
    `ResolverResult.Deferred` by the cycle-1 `LengthResolver` (the
    raw text rides along on the side; the slot itself stays
    `ComputedSlotTag.Unset`), and cycle 4's `ReadColumnWidth`
    returns null for those, so they don't trigger multicol dispatch
    via the column-width path. Authors who write
    `column-width: 12em` (the CSS Multi-column L1 §3.1 introductory
    example) currently fall through to ordinary block flow.
    Sub-cycle 5+ will resolve them against the cascaded font-size
    (em/rem) + containing block (percentages).
  - **Balance-result perf cache (F#6 — deferred from post-PR-#59
    review)**: the fit-search runs `O(log range) × columnCount`
    nested `BlockLayouter` dry-runs per multicol per page. When a
    multicol's content is unchanged across pages the result could
    be memoized per Box (key: rootBox + perColumnInlineSize +
    perColumnBlockSize + columnCount + carriedContinuation
    identity). Sub-cycle 2+ scope; the current per-page cost is
    acceptable for cycle 3 Hello World correctness over
    optimization.
  - **Pass-count benchmark guard**: a perf-bench-gated upper bound
    on the number of nested `BlockLayouter.AttemptLayout` calls per
    multicol per page. Sub-cycle 2+ scope alongside the F#6 cache —
    once the cache lands, the guard pins the cache hit-rate.
  - **Column balancing with explicit `height`** (CSS Multi-column L1
    §3.4): cycle 3 only balances `height: auto` multicols. When the
    author specifies an explicit `height` AND `column-fill: balance`,
    the spec calls for balancing within the explicit height
    constraint; cycle 3 conservatively falls back to serial fill to
    avoid the over-shrink drop-out failure mode. Sub-cycle 2+ work.
  - **`column-span: all`** (CSS Multi-column L1 §4): a child with
    `column-span: all` spans across all columns; cycle 1 has no
    column-span machinery.
  - **Column rules** (`column-rule-*` — CSS Multi-column L1 §5):
    the painter would draw a rule line between adjacent columns at
    the column gap's midpoint. Cycle 1 emits no rule fragments;
    the properties parse + cascade but have no painted effect.
  - **`column-gap` font-relative resolution**: cycle 1 hard-codes
    16 px for the `normal` initial value; sub-cycle 2 will resolve
    against the cascaded `font-size`.
  - **Fragmentation interaction** (`break-before` / `break-after` /
    `break-inside: avoid-column`): the cycle-1 `BlockLayouter`-as-
    column doesn't honor avoid-column constraints; only the regular
    block-level break properties (which the inner BlockLayouter
    already supports) apply.
- **Trigger** — corpus needs a multi-column layout, OR a user-reported
  case where multicol content vanishes (= the forced-overflow
  diagnostic fires).
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/MulticolLayouter.cs` — cycles 1-4
    implementation; cycle 2 adds the multi-page resume path via
    `MulticolContinuation`; cycle 3 + post-PR-#59 review hardening
    add `column-fill: balance` / `balance-all` via the binary-
    search fit-probe pipeline (`PreMeasureTotalSerialExtent` looped
    + margin-aware, `FindBalancedColumnBlockSize`, `FitsInNColumns`);
    cycle 4 derives the effective column count from
    `column-width` via `ComputeUsedColumnCount` + adds the
    `EmitSingleColumnFallthrough` path for derivedN == 1.
    Sub-cycle 2+ will add the F#6 fit-result cache + column rules +
    `column-span: all`.
  - `src/NetPdf.Layout/Layouters/ComputedStyleLayoutExtensions.cs`
    — cycle 3 adds `ReadColumnFill()` (returning `ColumnFillValue`)
    + `IsHeightAuto()` (post-PR-#59 hardening F#7 — correctly
    classifies Percentage + Calc heights as NON-auto; only Unset
    and Keyword slots are auto); cycle 4 adds `ReadColumnWidth()`
    (decoding `column-width` as a CSS px length, null on auto) +
    the static helper `ComputeUsedColumnCount(...)` (encoding the
    4 spec cases from CSS Multi-column L1 §3.3).
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — the dispatch
    site that recognizes multicol containers + invokes
    `MulticolLayouter`; cycle 2 adds the
    `MulticolContinuation`-via-`BlockContinuation.LayouterState`
    propagation pattern (main loop + recursion's depth==1 callback);
    post-PR-#59 hardening F#7 fixes the private static
    `IsHeightAuto` identically to the extension version; cycle 4
    extends `IsMulticolContainer` to fire on `column-width: <length>`
    AND `column-count: <int>` (the dispatch gate captures multicol
    intent; derivedN is computed once container geometry is known
    inside `MulticolLayouter`).
  - `src/NetPdf.Paginate/LayoutContinuation.cs::MulticolContinuation`
    — cycle 1 reserved the type; cycle 2 expands it to the
    `(NextChildIndex, ConsumedBlockSize, PerChildLayouterState)`
    contract.
- **Added** — Phase 3 Task 14 cycle 1; expanded in cycle 2; cycle 3
  Hello World shipped a 2-pass average-height balancing approach;
  post-PR-#59 review hardening replaced the heuristic with a binary-
  search fit-probe (F#1) + last-fragment semantics (F#2) + resume-
  aware pre-measure (F#3) + multi-window loop (F#4) + margin-aware
  extent (F#5) + correct `IsHeightAuto` (F#7) + corrected
  `ConsumedBlockSize` accumulator (F#8). F#6 perf-cache deferred to
  sub-cycle 2+. Cycle 4 ships `column-width`-derived used column
  count per CSS Multi-column L1 §3.3 + single-column degenerate
  fallthrough for derivedN == 1.
- **Removal condition** — column rules paint; `column-span: all`
  works. (Multi-level recursive multicol propagation shipped in
  Phase 3 Task 14 cycle 2 hardening Finding #1; column balancing
  shipped in cycle 3; `column-width` derived used count shipped in
  cycle 4.)

---

## float-continuation-propagation

- **ID** — `float-continuation-propagation`
- **Status** — `approximated` (in-flow continuations propagate cleanly
  through any recursion depth per Phase 3 Task 14 cycle 2 hardening
  Finding #1; floats remain out-of-flow per CSS 2.2 §9.5 and can't
  yet carry a continuation across pages).
- **Behavior** — When a float subtree (`float: left` / `float: right`
  containing block-level descendants) hosts a nested container whose
  pagination breaks mid-emission, the `BlockLayouter` recursion would
  produce a non-null `LayoutContinuation` for that subtree. Floats are
  out-of-flow per CSS 2.2 §9.5; propagating their continuation through
  the in-flow pagination machinery would require float-tracking
  continuation machinery (FloatManager-aware continuation state, float-
  fragment resume contract, BFC-snapshot restoration).
- **Task 19 (what landed)** — floats DON'T fragment across pages yet,
  so nested **grid** + **flex** containers inside a float now emit
  ATOMICALLY (the cycle-1 contract: all rows / items on one page,
  overflowing the page edge if tall) — LOSSLESS, the correct
  out-of-flow model, mirroring how a float with tall explicit content
  already force-overflows. Gated by `_inAtomicFloatSubtree` (set
  save/restore around the float recursion entry in `EmitFloat` /
  `EmitNestedFloat`), which suppresses the recursive-site
  paginatable-grid + paginatable-flex clamp/flag. In-flow content is
  byte-identical (the flag is only set inside a float). A regression
  test pins "float + 1000px grid on a 500px page → AllDone, no
  `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001`, rows past the page edge
  emitted".
- **Still truncates (+ diagnoses)** — nested **table** + **multicol**
  inside a float. Their wrapper sizing couples to the page budget
  (`PreMeasureTableIfNeeded` with `useDryRunCommittedHeight`; the
  MulticolLayouter paginates against the captured fragmentainer), so
  forcing them atomic needs a page-budget-decoupled measure pass
  (a separate cycle); until then the recursion return is discarded +
  `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001` fires at most once per page.
- **Missing** — atomic (or fragmenting) nested **table** + **multicol**
  inside floats; the full float-tracking continuation machinery
  (FloatManager state snapshot/restore across pages — the snapshot/
  restore API already exists; float-fragment resume contract; cross-
  page float overflow per CSS Fragmentation L3 §5) for true float
  fragmentation rather than atomic overflow.
- **Trigger** — corpus needs a float containing multicol/table that
  spans pages, OR a user-reported case where content inside a
  large float vanishes.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs::EmitFloat`,
    `EmitNestedFloat` — recursion sites that discard the return +
    emit the diagnostic.
  - `src/NetPdf.Layout/FloatManager.cs` — would gain the cross-page
    snapshot/restore contract.
  - `src/NetPdf.Paginate/LayoutContinuation.cs` — would gain a
    `FloatContinuation` record alongside `BlockContinuation` /
    `TableContinuation` / `MulticolContinuation`.
- **Added** — Phase 3 Task 14 cycle 2 hardening Finding #1 (this
  branch). The discard behavior pre-existed (cycle 1 had no return
  contract at all so the breaks were silently swallowed); the
  hardening just made the truncation visible via the new
  diagnostic.
- **Removal condition** — `FloatContinuation` ships + the float-
  emission path consumes / produces it; nested multicol/table
  pagination inside floats matches the in-flow behavior (clean
  multi-page splits with no `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001`).

---

## flex-layouter-features

- **ID** — `flex-layouter-features`
- **Status** — `approximated`. CSS Flexbox L1 is feature-complete for the common
  cases (W3C Flexbox conformance **100%**, 19/19 as of the post-`0.7.0-beta`
  sizing-residuals PR): all four `flex-direction` values, `flex-wrap` (incl.
  `wrap-reverse`), `justify-content` / `align-items` / `align-self` / `align-content`
  (positional + safe/unsafe overflow + **`align-items`/`align-self: baseline`** on the
  row cross axis), `flex-grow` / `flex-shrink` / `flex-basis` (length + auto +
  **`content` / `max-content` / `min-content` intrinsic keywords on the nowrap row main
  axis**) with the §9.7 step-4 min/max clamping iteration, the **`flex` shorthand** (§7.4
  grammar, via `FlexShorthandExpander`), `order`, `gap` / `column-gap` / `row-gap`
  gutters (consuming free space before grow/shrink + justify-content), explicit container
  `width` + `margin: 0 auto` centering, RTL `row` main-axis flip, anonymous-item
  wrapping, and multi-page container splitting. The residual approximations are enumerated
  under **Missing** below (intrinsic `flex-basis` on WRAP rows + `fit-content`,
  `align-content: baseline`, margin-box in the alignment / justify-content free-space
  math, RTL column MULTI-LINE line-stacking — the column-rtl per-item cross anchor
  SHIPPED — + vertical writing modes). Percentage gaps + percentage
  item min/max main-size resolve
  against the container content box in BOTH emission and the BlockLayouter
  pre-measure as of the `0.7.0-beta` sizing-residuals review (PR #206); a `%`
  row-gap on an auto-height column resolves to 0 per the indefinite-reference rule.
- **Behavior** — A block container with `display: flex` (=
  `BoxKind.FlexContainer`) or `display: inline-flex` (=
  `BoxKind.InlineFlexContainer`) lays out its direct block-level
  children along the main axis selected by `flex-direction`. Per
  Phase 3 Task 15 L4 + L5 the layouter honors all four
  `flex-direction` values **for LTR horizontal-tb** (the L1 default
  writing mode; per Phase 3 Task 15 L5 post-PR-#65 review F#1 the
  spec-correct axis mapping for RTL or vertical writing modes per
  CSS Flexbox §3.1 is L7+ scope — the L5 contract is LTR
  horizontal-tb only; L6 shipped `flex-wrap: wrap` without expanding
  the direction-pipeline scope): `row` (the L1-L3 default; main = inline
  axis, items flow left-to-right), `column` (L4 new; main = block
  axis, items stack top-to-bottom), `row-reverse` (L5 new; main =
  inline axis, but main-start moves to the inline-end edge so items
  pack at the right edge in reverse DOM order), and `column-reverse`
  (L5 new; main = block axis, but main-start moves to the block-end
  edge so items pack at the bottom in reverse DOM order). For column
  direction `justify-content` controls block-axis packing +
  `align-items` controls inline-axis placement (the axis swap is
  transparent to L2 + L3's alignment math — only the cursor axis +
  the property reads change). For the reversed variants per CSS
  Flexbox L1 §5.1, the per-item placement math (cross-axis alignment,
  stretch, justify-content start-offset + between-spacing) is
  unchanged from the non-reversed counterpart; only the FINAL
  main-axis offset assigned to each fragment is flipped around the
  container's main-extent — the spec-precise formula applied in
  `FlexLayouter`'s emission loop accounts for the wrapper's content-
  box origin:
  `actualMainOffset = (contentMainOffset + containerMainSize) -
  (mainCursor - contentMainOffset) - itemMainSize`, where
  `contentMainOffset` is the wrapper's content-box start on the main
  axis (padding/border-aware) and `mainCursor` is the natural
  non-reversed cursor position from the justify-content algorithm.
  The effect is that main-start and main-end swap per CSS Flexbox
  §5.1; items are placed using the same justify-content algorithm
  but their offsets are mirrored across the main-extent, yielding
  reverse DOM ordering in a single emission pass. Cross-axis behavior is
  unchanged for reversed variants (row-reverse still has block as
  cross axis; column-reverse still has inline as cross axis). Each
  item emits at its natural main-axis + cross-axis sizes from the
  direction-appropriate property (= width / height for row → main /
  cross; height / width for column). Items pack along the main-axis
  cursor; the cursor is offset by L2's `justify-content` start-offset
  + advances by `itemMainSize + betweenSpacing`. L2's
  `justify-content` honors the full matrix per
  CSS Box Alignment L3 §4.5 + §5.3: six base values (`flex-start`,
  `flex-end`, `center`, `space-between`, `space-around`,
  `space-evenly`) cross three overflow modes (default, `safe`,
  `unsafe`). On overflow (free-space < 0): distribution values fall
  back to safe-start; positional values keep their natural
  (possibly-negative) offset; `safe X` always falls back to safe-
  start; `unsafe X` honors the alignment even on overflow. Logical
  aliases (`start` / `end` / `left` / `right`) map to `flex-start` /
  `flex-end` under the L1 default LTR + `flex-direction: row`. L3's
  `align-items` honors the four base values per CSS Flexbox L1 §8.3
  + CSS Box Alignment L3 §6: `flex-start` (cross-start pack),
  `flex-end` (cross-end pack), `center` (cross-axis centering), and
  `stretch` (auto-sized items resized to fill the container's cross
  extent; explicitly-sized items keep their declared block-size per
  §7.2). `normal` resolves to `stretch` (the computed default).
  Logical aliases (`start` / `end` / `self-start` / `self-end`) map
  to `flex-start` / `flex-end` under the L1 default LTR +
  `flex-direction: row`. Compound `safe` / `unsafe` modifiers honor
  CSS Box Alignment L3 §5.3 (safe → safe-start fallback on overflow;
  unsafe → honor alignment on overflow; default → positional values
  keep natural offset on overflow). The container's cross-axis
  extent (`containerCrossSize`) is direction-dependent: row direction
  (cross axis = block axis) uses explicit `height` when set else
  max(item natural block-size) per CSS Flexbox L1 §9.4; column
  direction (cross axis = inline axis) uses the wrapper's
  content-inline-size (= available inline range from BlockLayouter's
  ConfigureEmission) — `width: auto` on a block-level flex container
  means "fill containing block" per CSS Sizing §3.4, NOT shrink-to-fit
  (inline-flex shrink-to-fit is L7+ scope; L6 shipped `flex-wrap:
  wrap` without expanding the inline-flex sizing scope).
  Production multi-page flex is **ACTIVE as of Task 16 cycle 4b**:
  paginatable row+wrap containers whose grown natural extent
  exceeds the remaining fragmentainer space are clamped (wrapper
  border-box → page-remaining-block) + dispatched with
  `allowPagination: true`. FlexLayouter packs lines up to the
  clamped budget + emits a `FlexContinuation` for the rest (CSS
  Flexbox L1 §10 fragmentation + CSS Fragmentation L3 §4.4
  progress rule). The `IsPaginatableFlex` predicate gates the
  clamp (row direction + wrap + non-wrap-reverse, mirroring
  FlexLayouter's `isRowNormalWrapPaginationSupported` predicate);
  column / wrap-reverse / nowrap fall through to the cycle-pre-4b
  atomic emit (with content overflowing the wrapper if too tall).
  **Per Phase 3 Task 15 L6** — `flex-wrap: wrap` ships in full per
  CSS Flexbox L1 §6.3 + §9.3 + §9.4: greedy line packing along the
  main axis (the first item on each line always lands even if it
  itself overflows the container; subsequent items wrap when adding
  would exceed `containerMainSize`); each line runs its OWN justify-
  content + align-items per CSS Flexbox L1 §6.3 ("each flex line is
  treated as the alignment container for its items along the cross
  axis"); auto cross-size = sum of line cross-extents
  (`PreMeasureFlexMultiLineCrossExtent` at the BlockLayouter pre-
  measure sites). Direction-agnostic — wrap works for both row +
  column. For column + wrap, an EXPLICIT block-size is required
  (auto block-size in column direction can't wrap in a single-pass
  measure; the L6 pre-measure skip rule prevents
  `PreMeasureFlexMainExtent` from growing past the declared height
  + defeating the wrap).
  **Per Phase 3 Task 15 L7 + post-PR-#67 hardening** — `align-content`
  ships the seven base values (`flex-start` / `flex-end` / `center` /
  `space-between` / `space-around` / `space-evenly` / `stretch`) per
  CSS Flexbox L1 §8.4 + CSS Box Alignment L3 §6, distributing wrapped
  lines along the cross axis on multi-line containers. The §8.4
  spec default `normal` resolves to `stretch` — definite-cross-sized
  multi-line containers grow each line by an EQUAL share of the free
  cross-space (= `freeCrossSpace / lineCount` per line) so the lines
  collectively fill the container. (Post-PR-#67 F#5 — original
  deferral wording said "proportionally"; that incorrectly implied
  weighted distribution. The spec defines equal-share growth.) Items
  on a stretched line use the LARGER (stretched) cross extent for
  their align-items math. **Post-PR-#67 F#1 single-line gate
  correction:** the single-line-vs-multi-line boundary is
  `flex-wrap: nowrap` per CSS Flexbox §9.4 — NOT `lineCount <= 1`.
  A wrapping container that happens to produce one line is still a
  multi-line container, and align-content (including the §8.4 stretch
  default) applies to it. The `ComputeAlignContentOffsets` helper
  short-circuits only when `lineCount == 0 || !isWrapping`.
  **Post-PR-#67 F#2 per-mode overflow handling:** when sum of line
  cross extents > container cross extent, behavior now mirrors the
  L2 `ComputeJustifyContentOffsets` pattern per CSS Box Alignment L3
  §5.3: `safe X` falls back to safe-start regardless of value family;
  `unsafe X` honors the natural (possibly-negative) offset; default
  mode gives distribution values + stretch the safe-start fallback
  while positional values keep their natural offset (allowing items
  to overflow equally on both sides for `center`). **Post-PR-#67 F#6
  baseline keyword family:** the three `<baseline-position>` keywords
  (`baseline` / `first baseline` / `last baseline`) admitted by CSS
  Box Alignment L3 §6.3 are added to BuildAlignContentTable (29-entry
  table) but currently approximate to `stretch` — proper baseline
  alignment is text-shaping-integration scope (L9+; see the bullet
  below).
  **Per Phase 3 Task 15 L8** — `flex-grow` / `flex-shrink` /
  `flex-basis` ship the §7 + §9.7 flexibility algorithm. Per line:
  compute each item's hypothetical main-size from its `flex-basis`
  (Auto delegates to the declared main-size; Content/MaxContent/MinContent
  are content-sized — measured on the nowrap ROW main axis, block-extent on
  the COLUMN axis; LengthPx uses the explicit pixel value; Percentage
  resolves against the container's main-size); compute `freeMainSpace = containerMainSize -
  sum(hypothetical)`; if positive AND any item has `flex-grow > 0`,
  each item grows by `(item.flexGrow / sumFlexGrow) * freeMainSpace`;
  if negative AND any item has `flex-shrink > 0`, each item shrinks
  by `(item.flexShrink * hypothetical / sumScaledShrinks) *
  |freeMainSpace|`. Resolved main-sizes feed the per-line emission
  loop's main-axis placement (replacing the L1-L7
  `ReadLengthPxOrZero(mainSizeProperty)` direct read). Each line's
  `LineMainSize` is recomputed post-flex so `justify-content`'s
  freeSpace calculation matches the flexed layout. The cascade-side
  `flex-grow` (Number type, default 0) + `flex-shrink` (Number type,
  default 1) properties were already in properties.json; L8 joined
  the `flex-basis` (FlexBasis type, default `auto`) property to the
  LengthResolver dispatch (admitting `auto` / `content` keywords +
  the `<length-percentage>` production per §7.2). Three new reader
  extensions (`ReadFlexGrow` / `ReadFlexShrink` / `ReadFlexBasis`)
  + typed `ResolvedFlexBasis` (Kind + Value) mirror the L2/L3/L7
  resolved-* patterns.
- **Missing** —
  - ~~`flex-wrap: wrap-reverse`~~ — **shipped in Phase 3 Task 15 L11.**
    Per CSS Flexbox L1 §6.3 wrap-reverse permutes cross-start +
    cross-end. The FlexLayouter reverses the `lines` list after
    PackLines when `flex-wrap: wrap-reverse` AND there are 2+ lines;
    item DOM order within each line is preserved. align-content
    distribution applies to the reversed list per §8.4. The L6
    hardening F#4 `LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001`
    diagnostic no longer fires (the approximation is gone). The
    diagnostic code remains registered in `PaginateDiagnosticCodes`
    for backward-compat / cross-reference.
  - **`align-content` proper `<baseline-position>` alignment** (CSS
    Box Alignment L3 §6.3): post-PR-#67 F#6 admits the three baseline
    keywords (`baseline` / `first baseline` / `last baseline`) into
    BuildAlignContentTable but the reader maps all three to `stretch`
    as a safe approximation. Proper baseline alignment requires
    text-shaping integration to align the LINES (not the items) by their
    baselines on the cross axis; the companion **`align-items: baseline`
    SHIPPED** (Flex L1 completion — see below) but `align-content: baseline`
    still maps to `stretch`. The cascade slot is lossless so pre-authored
    `align-content: baseline` declarations activate the new behavior
    without a re-author.
  - Writing-mode + column-cross-axis `direction` integration for
    `flex-direction` axis mapping (CSS Flexbox §3.1). **Shipped** (task 6):
    `flex-direction: row` / `row-reverse` under `direction: rtl` flip the
    physical MAIN axis right-to-left (row+rtl ≡ row-reverse+ltr) —
    FlexLayouter XORs its reverse flag via `DirectionStyleExtensions.IsRtl`,
    pinned by `Rtl_row_flips_main_axis_like_row_reverse_ltr`. **Also shipped**
    (residual long-tail): the COLUMN per-item cross anchor under RTL — a
    `flex-direction: column; direction: rtl` container's cross axis is the
    inline axis, so RTL permutes cross-start/cross-end; the per-item
    `align-items`/`align-self` anchor now flips (`isCrossAxisReversed =
    isWrapReverse ^ isColumnRtl`, fed to `ComputeAlignItemsPlacement`), so
    `align-items: flex-start` right-anchors and `flex-end` left-anchors (pinned
    by `Column_rtl_align_items_flex_{start,end}_anchors_at_the_inline_*`).
    **Still deferred**: the line-to-line STACKING order for a MULTI-LINE
    column-rtl `wrap` (the `CrossAxisFlow` reversal stays on `isWrapReverse`
    alone to avoid double-counting the single-line case — items within each
    line are correctly anchored, but the lines stack left-to-right not
    right-to-left), and all VERTICAL writing modes (`writing-mode` is not yet
    a registered property; `row` in vertical-rl swaps the main + cross axes
    onto the rotated block + inline directions).
  - Outer-main-size + auto-margins in `justify-content` free-space
    calculation (CSS Flexbox L1 §9.5): L2's pre-pass sums only
    declared `width`, ignoring item margins / padding / borders /
    auto margins. Per spec the free-space calculation uses each
    item's resolved margin-box main-axis size, and auto margins
    consume free space BEFORE `justify-content` distributes the
    remainder. Sub-cycle 3+ will add the outer-size pre-pass.
    Until then, items with non-zero margins will produce slightly-
    off `justify-content` placements (e.g., space-between will
    leave less between-item space than the spec dictates).
  - Writing-mode-aware `left` / `right` mapping for
    `justify-content` (L2 maps both to `flex-start` / `flex-end`
    under the L1 default LTR + `flex-direction: row`; L3+ will
    resolve against the box's writing-mode + direction)
  - ~~`align-items: baseline` / `first baseline` / `last baseline`~~ —
    **shipped (Flex L1 completion).** Per CSS Box Alignment L3 §6.2 + CSS
    Flexbox L1 §8.3 the three `<baseline-position>` keywords decode to
    `AlignItemsValue.Baseline` (first-baseline alignment for all three —
    last-baseline grouping is a later refinement) and `align-self: baseline`
    folds + resolves against the container. For a ROW container each
    baseline-aligned item is shifted on the block (cross) axis so its first
    text baseline sits on the line's max baseline (the item's first baseline
    comes from `BufferingMeasureSink.FirstBaselineFromOrigin`; an item with no
    line box synthesizes from its cross-end edge per §8.5). **Still deferred**:
    the COLUMN cross axis (inline) falls back to flex-start — a first baseline
    on the inline axis needs vertical-text metrics.
  - `align-items: anchor-center` (CSS Anchor Positioning) — out of
    scope for Flexbox L1; the L3 decoder maps it to `stretch`. Sub-
    cycle L4+ (or later — anchor positioning is a separate spec)
    will pick this up.
  - ~~`align-self` per-item alignment override~~ — **shipped in
    Phase 3 Task 15 L9.** Per CSS Box Alignment L3 §4.3 the
    FlexLayouter reads <c>item.Style.ReadAlignSelf()</c> for each
    item + folds it against the container's <c>align-items</c> via
    <c>ResolveAgainstContainerAlignItems</c>; the cascade default
    <c>auto</c> preserves the container-only L1-L8 behavior.
    Per Phase 3 Task 15 L10 post-PR-#70 review F#5 — this bullet
    was accidentally re-introduced in the L10 hardening but L9 had
    already removed it; restored to its post-L9 "shipped"
    annotation.
  - Baseline-family overflow semantics + production parser
    recovery refinement for compound `align-items` keywords — L3
    decodes the compound `<overflow-position> <self-position>`
    forms (indices 13-26) into the `OverflowAlignmentMode` channel
    + applies the spec-correct positional-value behavior per CSS
    Box Alignment L3 §5.3 (same approach as L2's
    `justify-content`); per Phase 3 Task 15 L3 post-PR-#63
    hardening F#3 the `CssPreprocessor.KnownDroppedProperties`
    table now lists `align-items` so AngleSharp.Css's drop of
    compound keywords is recovered (same precedent as
    `justify-content`). Sub-cycle L4+ will refine the overflow
    semantics for the deferred-value families (e.g., when baseline
    alignment lands its overflow semantics differ from positional
    values'), and revisit the preprocessor entry if AngleSharp.Css
    upgrades its parser to recognize compound `align-items`
    natively.
  - **Cross-axis margins + auto-margins for align-items** (CSS
    Flexbox L1 §8.4): L3's alignment math operates on the item's
    border-box; the spec uses the item's margin-box and lets
    cross-axis `margin: auto` override `align-items` /
    `align-self`. Sub-cycle L4+ scope — requires plumbing margin
    reads through FlexLayouter. Until then, items with non-zero
    cross-axis margins produce slightly-off align-items
    placements. Pinned by the
    `L3_hardening_known_gap_cross_axis_margins_ignored_in_alignment`
    test — when sub-cycle L4+ ships the margin-box math, that
    test should fail + this bullet should be removed.
  - **Stretch with margin-box + min/max constraints** (CSS Flexbox
    L1 §7.2): L3 stretch sets `BlockSize = containerCrossSize` for
    auto-cross-sized items, ignoring item margins and min-height /
    max-height constraints. The spec computes stretch as the cross
    margin-box size with min/max clamps. Sub-cycle L4+ scope.
    Pinned by the
    `L3_hardening_known_gap_stretch_ignores_min_max_constraints`
    test — when sub-cycle L4+ ships the clamping, that test
    should fail + this bullet should be removed.
  - ~~**Explicit-width honoring for flex/grid containers**~~ —
    **shipped in the flex/grid gap PR (container-width cycle).**
    Added `BoxKind.FlexContainer` + `BoxKind.GridContainer` to the
    `ResolveInFlowBorderBoxInlineSize` gate, so an explicit `width`
    on a flex/grid container becomes its border-box width (feeding
    `FlexGeometryHelper` / `GridGeometryHelper`'s content inline
    size). Alignment, flex-shrink, flex-wrap, and fr track sizing
    now run against the declared width, not the page width. The
    three pins flipped to the spec-correct behavior:
    `L4_hardening_column_explicit_width_smaller_than_page_centers_correctly`
    un-Skip'd (item centers in 200 → X=50); the `..._known_gap_...`
    pin removed; `L6_narrow_flex_in_wide_page_wraps_per_declared_width`
    now asserts 2 lines. Conformance `flex-explicit-container-width`
    + `flex-explicit-width-center` + `flex-explicit-width-shrinks-items`
    + `grid-explicit-container-width` pass. (No real-document output
    changed — confirmed by the RealDocuments guard.)
  - ~~`order` property~~ — **shipped in Phase 3 Task 15 L10.** New
    `order` Integer property (default 0, applies_to FlexItems) +
    `ReadOrder` extension + `GetFlexChildrenInOrderSequence` shared
    helper. The FlexLayouter's `PackLines` /
    `ResolveFlexibleMainSizes` / emission loop AND the BlockLayouter's
    `PreMeasureFlexMultiLineCrossExtent` all walk the sorted sequence
    via `_sortedFlexChildIndices`; FlexLine.FirstItemIndex is now a
    POSITION in the sorted sequence (not a DOM-children index).
    Negative orders + equal-order DOM-stable ordering both supported.
    Fast-path short-circuit returns DOM order when no item declares
    a non-zero order, preserving L1-L9 behavior verbatim.
  - ~~**`flex` shorthand parser** (CSS Flexbox L1 §7.4)~~ — **shipped in
    Phase 3 Task 15 L13** (`FlexShorthandExpander`, wired into
    `CssPreprocessor`). The full §7.4 grammar expands to the three longhands:
    `none` / `auto` / `<number>` / `<basis>` / the two- and three-value forms.
    `flex: 1` → `flex-grow: 1; flex-shrink: 1; flex-basis: 0` end-to-end.
  - **`flex-basis: min-content` / `max-content` / `content`** intrinsic-sizing
    keywords (CSS Flexbox L1 §7.2 + §9.2.3 + CSS Sizing L3 §5.1) — **shipped
    (Flex L1 completion) for the NOWRAP ROW main axis.** `LengthResolver` admits
    the keywords (recovered through `CssPreprocessor` since AngleSharp.Css drops
    the value); `FlexLayouter.BuildRowIntrinsicMainBaseSizes` measures each
    item's max-content (no wrap pressure) / min-content (maximal wrap pressure)
    inline extent as the §9.7 flex base size, built identically by the emission
    and the BlockLayouter row-flex height pre-measure so they can't desync.
    `content` ≡ max-content per §9.2.3. The COLUMN axis content-sizes
    `content`/`max-content`/`min-content` via the existing block-extent measure.
    **Still deferred**: the WRAP row main axis (line-breaking depends on the base
    size in the shaper-less multi-line pre-measure), `fit-content` /
    `fit-content(<length-percentage>)`, and a **perf follow-up** (PR #208 [P3]) —
    the intrinsic measure lays each item into a full `BufferingMeasureSink`
    fragment list when only `ContentInlineExtent` is read, and the emission +
    pre-measure measure the same items twice; an extent-only sink or a
    measure-share across the two passes would trim allocations for documents with
    many intrinsic-basis flex items (low impact — the path is opt-in + rare).
  - Also baseline + `flex-wrap: wrap-reverse` (PR #208 Copilot review): a ROW
    baseline item under wrap-reverse falls back to flex-start rather than mirroring
    the baseline shift relative to the swapped cross-start, and a baseline
    down-shift in a WRAP auto-height container can under-size the wrapper (the
    multi-line cross-extent pre-measure is shaper-less, so it can't mirror the
    baseline-adjusted extent the nowrap pre-measure does).
  - **§9.7 step-4 min/max clamping** ✅ **shipped in L12** (CSS Flexbox L1
    §9.7 + §9.5) — `ResolveFlexibleMainSizes` now delegates per line
    to `ResolveLineWithMinMaxClamping`, which implements the full
    iterative algorithm: each iteration recomputes remaining
    free-space (excluding frozen items), redistributes among
    unfrozen items by flex-grow / scaled-shrink, clamps each
    unfrozen item to `[min-main-size, max-main-size]`, tracks
    `totalViolation`, and freezes min-violators
    (`totalViolation > 0`) or max-violators (`totalViolation < 0`)
    per spec. Convergence is guaranteed in ≤ `itemCount + 1`
    iterations. The L8 known-gap test
    `L8_known_gap_min_width_does_not_clamp_resolved_size_yet` is
    flipped to `L12_min_width_clamps_resolved_shrink_per_spec_step_4`
    + asserts the spec-correct clamped sizes. Known L13+ gap:
    `min-width: auto` (the cascade default for flex items) per CSS
    Sizing L3 §5.5 resolves to the item's intrinsic content size;
    L12's `ResolveFlexItemMinMaxMainSize` returns 0 for non-LengthPx
    min slots (= a conservative floor pending intrinsic-sizing
    integration). Percentage min/max-width also defers to L13+
    (needs per-item container main-size resolution at the resolver
    site).
  - **Shared `FlexItemSizing` model unification** (post-PR-#68
    architecture recommendation): the L8 post-PR-#68 hardening F#1
    extracted `ResolveFlexItemHypotheticalMainSize` to a shared
    extension on `Boxes.Box` so PackLines + ResolveFlexibleMainSizes
    + BlockLayouter's pre-measure all consume identical hypothetical
    sizes. A broader refactor would lift `FlexItemSizing { Box,
    ChildIndex, FlexBaseSize, HypotheticalMainSize, FlexGrow,
    FlexShrink, CrossSize }` into a small read-once struct shared
    across all three call sites, eliminating the repeated style reads
    + making the §9.7 step-4 min/max-clamp iteration easier to add.
    L9+ scope; the L8 extension is sufficient to close line-boundary
    drift but the struct unification is a follow-up optimization.
  - ~~**`flex-basis: content` proper implementation** (CSS Flexbox L1
    §7.2.1)~~ — **shipped (Flex L1 completion).** `Content` (≡ max-content per
    §9.2.3) now forces the intrinsic content size REGARDLESS of the declared
    width/height on the nowrap ROW main axis (measured via
    `FlexLayouter.BuildRowIntrinsicMainBaseSizes`) and the COLUMN axis (block
    extent); e.g. `width: 200; flex-basis: content` produces the intrinsic
    content size (NOT 200). The old pin flipped to
    `Flex_basis_content_uses_intrinsic_content_size_ignoring_declared_width`.
    **Still deferred**: the WRAP row main axis + `fit-content`.
  - ~~Anonymous flex-item wrapping for inline-level / text children~~
    ✅ shipped in Phase 3 Task 15 L15 (PR #75). The L1-L14 cycle-1
    skip was replaced by `BoxBuilder.FixupFlexAnonymousItems`
    which blockifies inline element children + wraps TextRun
    runs into anonymous block flex items per CSS Flexbox L1 §4.
    See the L15 entry in the "Removal condition" trigger list
    below for the historical fix.
  - **Multi-page flex container splitting** (Task 16, in progress;
    cycles 1-3 shipped the FlexLayouter resume contract + scaffolded
    BlockLayouter dispatch propagation; cycle 4a extracted the
    `DispatchFlexInner` helper; **cycle 4b shipped the
    pre-break-check paginatable-flex extent clamp + flipped
    `allowPagination: true` at both dispatch sites**. The remaining
    cycle-4 follow-on items below are tightening, not blockers for
    production multi-page flex:
    - ✅ **P1 #1 (PR-#79) shipped in cycle 4b**: paginatable-flex
      extent clamp at BOTH dispatch sites. New
      `IsPaginatableFlex(box)` predicate (row + wrap +
      non-wrap-reverse, mirroring `FlexLayouter`'s
      `isRowNormalWrapPaginationSupported`) gates the clamp;
      eligible containers whose grown natural extent overflows the
      remaining fragmentainer space have `borderBoxBlockSize` /
      `childBorderBoxBlockSize` clamped to the page-remaining-block
      + the `paginateFlex*ForChild` flag flips
      `allowPagination: true` in `DispatchFlexInner`. FlexLayouter
      packs lines up to the clamped budget + emits a
      `FlexContinuation` for the rest; the chain-walk-propagation
      already wired in cycles 2-3 carries the continuation up via
      `PageComplete(BlockContinuation(LayouterState=FlexContinuation))`.
    - **P1 #2 (PR-#80) — partial close in cycle 4b**: the recursive
      site's outbound propagation IS now active (the cycle-3
      scaffolding fires when the cycle-4b clamp turns
      `allowPagination` ON). The INBOUND chain-walk (= read
      `incomingBlockChain.LayouterState` for a `FlexContinuation`
      leaf when the chain reaches a nested flex container) is still
      null at `EmitBlockSubtreeRecursive`'s nested flex branch
      (line ~3360). For deeply-nested flex inside multi-page splits
      where the resume page needs to forward an INNER
      FlexContinuation into a deeper level, the chain-walk is
      required; the cycle-4b production test is shallow enough that
      the cycle-2 direct-dispatch chain-walk handles it. Cycle 4c
      will add the recursive-site chain-walk for the multi-level
      case.
    - ✅ **P1 #3 (PR-#79 + PR-#80) shipped in cycle 4b**:
      `Task16_cycle2_production_html_flex_container_splits_across_two_pages`
      is now `[Fact]` (not `Skip`) and passing through the full
      HTML → cascade → BoxBuilder → BlockLayouter → FlexLayouter
      pipeline. The chain-walk pattern matches
      `MulticolLayouterProductionTests`' walker.
    - ✅ **P2 #4 (PR-#79) verified closed-by-implementation in
      cycle 4 closeout**: the cycle-4b derivation already uses
      `topShift` (= the post-margin-collapse delta) at the outer
      site + `childBlockOffset` (= post-collapse absolute) at the
      recursive site, NOT `effectiveTopGap` (= the
      pre-subtraction value the deferral worried about).
      `Task16_cycle4_closeout_margin_collapse_before_flex_still_round_trips`
      pins the contract: a preceding sibling with margin-bottom + a
      flex container with margin-top still rounds-trips through the
      paginated path with all items emitting exactly once.
    - 🚧 **P2 #5 (PR-#79) contract shipped in cycle 4e + hardened
      post-PR-#86 review** —
      `FlexContinuation.EmittedBlockExtent` field added + populated
      by FlexLayouter on PageComplete with the **TRUE occupied
      block extent** (= the content-cross-box 0-based bottom of the
      deepest emitted line, tracked as `maxEmittedCrossBottom` in
      the emission loop). The value INCLUDES align-content's
      `lineStartOffset` + `lineBetweenSpacing` contributions
      (space-between / space-around / space-evenly / center /
      flex-end alignment families) — NOT a naive
      `sum(LineCrossSize)`. The field's value is also defensively
      validated in the `FlexContinuation` constructor (rejects
      NaN / ±Infinity / negative). The BlockLayouter consumer side
      is **cycle 4f scope** because of the z-order constraint: the
      wrapper fragment must precede its children in the sink's
      fragment list (= painter draw order), so the wrapper emit
      can't simply move to post-dispatch. Cycle 4f will add a
      sink-mutation or pre-emit-with-backfill API to let the
      wrapper's BlockSize be retro-adjusted to the actual emitted
      extent.
    - ✅ **P3 #7 (PR-#79 + PR-#80) shipped in cycle 4a (PR #82)**:
      `DispatchFlexInner` helper now used by BOTH direct +
      recursive paths to eliminate drift between them. 135 + 107
      LOC consolidated; the helper owns FlexLayouter +
      BreakResolver lifetime via `using var`.
    - ✅ **P3 #8 (PR-#79) shipped in cycle 4c**: shared
      `FlexLinePacker` extracted to
      `src/NetPdf.Layout/Layouters/FlexLinePacker.cs`. Both
      `FlexLayouter.PackLines` (8-line delegating forward) +
      `BlockLayouter.PreMeasureFlexMultiLineCrossExtent` (calls Pack
      + sums `LineCrossSize`) now consume one shared
      implementation. `FlexLine` promoted from FlexLayouter's
      private nested record struct to internal at the namespace
      level. 6 direct unit tests in `FlexLinePackerTests` pin the
      algorithm contract.
    - ✅ **P2 from PR-#82 review #2 shipped in cycle 4d**:
      extracted `FlexGeometryHelper` to
      `src/NetPdf.Layout/Layouters/FlexGeometryHelper.cs`. The 3
      dispatch sites (outer, recursive, forced-overflow re-route)
      each now call
      `FlexGeometryHelper.ComputeContentGeometry(box, borderBox*,
      offset*) → FlexContentGeometry`. Pattern mirrors
      `MulticolGeometryHelper`; simpler since flex's
      content-block-size always derives from the wrapper's
      border-box (no auto-height/fragmentainer-remaining branch).
      4 direct tests in `FlexGeometryHelperTests` pin the math.
    - ✅ **P2 from PR-#82 review #3 closed via documentation**:
      `DispatchFlexInner`'s hardcoded fresh `BreakResolver` +
      `LastResort` strategy are the established nested-layouter
      isolation pattern (= mirrors `TableLayouter`'s per-cell +
      `MulticolLayouter`'s per-column resolver isolation). The
      review's "Either parameterize OR document why" — the helper's
      xmldoc explains both rationale + the trigger condition for
      future parameterization (= "When… we discover the inner level
      legitimately wants its own strategy / resolver, parameterize
      this helper at that point"). Cycle 4b's production pagination
      ships without any caller demanding parameterization; the
      deferral closes as docs-only. If a future caller surfaces a
      legitimate need (= e.g., a profile-driven optimizer wants
      checkpoint sharing across nested layouters), reopen the
      deferral + add the parameters at that point.

    **Cycle 4 execution order** — UPDATED post-cycle-4-closeout
    (PR #87). All originally-planned items 1-8 are shipped or
    closed-by-implementation/documentation. The remaining open
    follow-ons (= NOT included in cycle 4 closeout) are tracked in
    deferrals.md as future work:
    * Multi-level inbound recursive FlexContinuation chain-walk for
      deeper-than-shallow nesting — speculative without a forcing
      test (cycle 4b's shallow case + cycle-4-closeout's margin
      regression cover current production shapes).
    * Cycle 4f BlockLayouter consumes `EmittedBlockExtent` for
      wrapper resize / ConsumedBlockSize precision — blocked on
      sink-mutation or pre-emit-with-backfill API (z-order
      constraint documented on FlexContinuation).
    1. ✅ **Extract `DispatchFlexInner`** — shipped in cycle 4a
       (PR #82).
    2. ✅ **Add pre-break-check paginatable-flex dispatch** — shipped
       in cycle 4b (PR #83). The clamp lives at the END of the flex
       pre-grow block (NOT before the resolver consult) — the
       end-of-pre-grow site has all the variables in scope + makes
       the chunk-for-break-check naturally pass through the
       Continue path with `allowPagination: true`. Mathematically
       equivalent to a pre-break-check intercept; structurally
       simpler.
    3. ✅ **Wire inbound recursive FlexContinuation chain-walk for
       the shallow case** (PR-#83 review P1 #1) — shipped in cycle 4b
       hardening. The recursive flex dispatch in
       `EmitBlockSubtreeRecursive` peels `incomingBlockChain` to
       extract a `FlexContinuation` leaf at the resume-at child.
       The deeper multi-level case (= a FlexContinuation reached
       through a chain that includes a flex container's parent flex
       container) is still deferred — current production tests
       don't exercise that nesting depth.
    4. ✅ **Compute margin-collapse-aware `pageRemainingBlock`**
       (P2 #4) — closed-by-implementation in cycle 4 closeout. The
       cycle-4b derivation already uses post-collapse values
       (`topShift` + `childBlockOffset`); regression test
       `Task16_cycle4_closeout_margin_collapse_before_flex_still_round_trips`
       pins the contract.
    5. 🚧 **Return emitted-fragment block extent from FlexLayouter**
       (P2 #5) — contract SHIPPED in cycle 4e (PR #86): new
       `FlexContinuation.EmittedBlockExtent` field populated on
       PageComplete. The BlockLayouter consumer-side wrapper-resize
       is cycle 4f scope (= z-order constraint requires sink
       mutation or pre-emit-with-backfill).
    6. ✅ **Unskip the production-pipeline test** (P1 #3) — shipped
       in cycle 4b.
    7. ✅ **Forced-overflow flex re-route via `DispatchFlexInner`**
       (PR-#83 review P1 #2) — shipped in cycle 4b hardening.
       Forced-overflow flex containers (ineligible for the clamp,
       e.g., column / wrap-reverse / nowrap) now dispatch atomically
       through the helper instead of dropping items via
       `EmitBlockSubtreeRecursive` (which doesn't own flex inner
       layout).
    8. ✅ **Shared `FlexLinePacker`** (P3 #8) — shipped in cycle 4c
       (PR #84). One static `Pack` / `SumCrossExtent` (streaming
       per PR-#84 review P2 #1) shared between BlockLayouter
       pre-measure + FlexLayouter emission. `FlexLine` promoted to
       internal at the namespace level. Axis mapping consolidated
       to `FlexDirectionValueExtensions.GetAxisProperties` (PR-#84
       review P3 #5).

    `FlexContinuation` exists in
    `src/NetPdf.Paginate/LayoutContinuation.cs`; the data flow is
    fully active for the production-shallow case post-cycle-4b,
    with deeper-nesting + pixel-perfect cursor advancement tracked
    as the active follow-ons #4, #5 above.)
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/FlexLayouter.cs` — the layouter
    itself.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — the `IsFlexContainer`
    predicate + the outer-loop + recursion-site dispatch into
    `FlexLayouter`.
  - `src/NetPdf.Layout/Boxes/DisplayMapper.cs` — `display: flex`
    `/ inline-flex` → `BoxKind.FlexContainer` /
    `BoxKind.InlineFlexContainer`.
  - `src/NetPdf.Css/properties.json` — the `align-content`,
    `align-items`, `align-self`, `flex-direction`, `flex-wrap`,
    `flex-grow`, `flex-shrink`, `flex-basis`, `justify-content`,
    `order`, `min-width`, `max-width`, `min-height`, `max-height`
    properties (all cascade-parsed + honored at the layouter
    through L17; `wrap-reverse` cross-axis reversal shipped in L11
    with the proper SWAP formula).
- **Trigger** — L2 picked up `justify-content`; L3 picked up
  `align-items` (base values + stretch); L4 picked up
  `flex-direction: column` + the F#1 hardening for column auto-
  height wrappers; L5 picked up `flex-direction: row-reverse` +
  `column-reverse` (the offset-flip transform at the per-item
  emission site); L6 picked up `flex-wrap: wrap` (multi-line
  greedy packing + per-line align math + sum-of-lines auto cross-
  size); L7 picked up `align-content` (multi-line cross-axis
  distribution per CSS Flexbox L1 §8.4 + CSS Box Alignment L3 §6
  — the seven base values + §8.4 stretch default; post-PR-#67
  hardening F#1/F#2 added the multi-line gate per §9.4 and per-mode
  overflow handling per §5.3; F#6 admitted the
  `<baseline-position>` triple as a stretch approximation). Sub-
  cycle L8 picked up `flex-grow` / `flex-shrink` / `flex-basis` (the
  §7 + §9.7 flexibility algorithm — see the L8 entry above). Sub-
  cycle L9 picked up `align-self` (per-item alignment override per
  CSS Box Alignment L3 §4.3); sub-cycle L10 picked up the `order`
  property (per CSS Flexbox L1 §5.4 — items reorder via stable sort
  by (order, DOM-index)); L11 picked up `flex-wrap: wrap-reverse`
  (proper cross-axis SWAP at offset-computation time per CSS Flexbox
  L1 §6.3 — see PR-#71 hardening); L12 picked up the §9.7 step-4
  min/max-width clamping iteration (closing the L8 known-gap pin).
  L13 picked up the `flex` shorthand parser (CSS Flexbox §7.4)
  via a new `FlexShorthandExpander` wired into the preprocessor's
  recovery pass — closes the gap where AngleSharp.Css 1.0.0-beta.144
  only partially handles the shorthand (handles `flex: <number>`
  but not `flex: none` / `auto` / `<basis>` / two- and three-value
  forms). L14 picked up the `CrossAxisFlow` refactor — pure refactor
  extracting the wrap-reverse swap formula out of FlexLayouter's
  emission loop into a `record struct CrossAxisFlow` with one
  method (`PhysicalLineOffset`). Closes the L11 post-PR-#71 F#9
  TODO seam; zero behavior change; 7 new direct unit tests pin
  the swap contract. L15 picked up anonymous-flex-item wrapping
  per CSS Flexbox §4 (`BoxBuilder.FixupFlexAnonymousItems`);
  L16 picked up the `flex-flow` shorthand parser via the new
  `FlexFlowShorthandExpander`; L17 picked up proper cascade
  source-order tracking for shorthand-vs-explicit-longhand
  conflicts (§5 importance + §7.4 ordering). Task 16 cycles
  1-4 shipped end-to-end multi-page flex pagination:
  cycles 1-3 wired the FlexLayouter resume contract +
  `FlexContinuation` data flow + BlockLayouter dispatch
  scaffolding; cycle 4a extracted `DispatchFlexInner`;
  cycle 4b shipped the paginatable-flex extent clamp +
  flipped `allowPagination: true` (= production multi-page
  split is ACTIVE; the cycle-2 production test is unskipped
  + passing); cycle 4c extracted the shared `FlexLinePacker`
  (DRY refactor). The remaining L18+ work picks up proper
  `<baseline-position>` alignment for both `align-items` and
  `align-content`, `min-width: auto` intrinsic resolution per
  CSS Sizing L3 §5.5, and `flex-basis: content` proper
  intrinsic-sizing integration — all blocked on per-item
  content-size measurement, which is the underlying L19+
  intrinsic-sizing deferral.
- **Added** — Phase 3 Task 15 cycle 1 (Hello World).
- **Removal condition** — Sub-cycle L11+ ships the remaining
  deferred features (wrap-reverse / proper baseline alignment /
  `flex` shorthand / §9.7 min-max clamp / multi-page split /
  anonymous flex item). L6 shipped `flex-wrap: wrap`; L7 shipped
  `align-content` (base values + §8.4 stretch default + post-PR-#67
  per-mode overflow handling); L8 shipped the §7 + §9.7 flexibility
  algorithm; L9 shipped `align-self`; L10 shipped `order`; L11
  shipped `flex-wrap: wrap-reverse` (with the post-PR-#71 cross-axis
  swap hardening); L12 shipped the §9.7 step-4 min/max-width
  clamping iteration; L13 shipped the `flex` shorthand parser
  (CSS Flexbox §7.4) via a new `FlexShorthandExpander` wired into
  the preprocessor's recovery pass; L14 shipped the `CrossAxisFlow`
  refactor — extracted the wrap-reverse swap math out of
  FlexLayouter's emission loop into a one-method record
  (`PhysicalLineOffset`) so the swap state has a named owner +
  future writing-mode work picks up cleanly; L15 shipped
  anonymous flex-item wrapping per §4 — BoxBuilder's new
  `FixupFlexAnonymousItems` pass blockifies inline element
  children + wraps TextRun runs into anonymous block-level flex
  items + drops whitespace-only TextRuns per §4; L16 shipped
  the `flex-flow` shorthand parser (CSS Flexbox §6.1) via a new
  `FlexFlowShorthandExpander` wired into the preprocessor's
  recovery pass (mirrors L13's `flex` shorthand pattern). Proper
  `<baseline-position>` alignment + `min-width: auto` intrinsic
  resolution + multi-page flex split (`FlexContinuation`) are
  the natural L17+ candidates.

---

## fuzzing-infrastructure

- **ID** — `fuzzing-infrastructure`
- **Status** — `not-started`.
- **Behavior** — Phase A-D's regression corpus + threat-model coverage
  (215+ security tests) is the primary defense surface. No
  coverage-guided fuzzing is wired.
- **Missing** — SharpFuzz + AFL++ harnesses for `HtmlParsingHost`,
  `CssPreprocessor` / `CssParserAdapter`, `SelectorCompiler`,
  `CalcResolver`, `VarSubstitution`, `ImageSafetyValidator`,
  `FontSafetyValidator`, and `PdfPreflightValidator`. CI gate, crash
  triage runbook, 30-day no-new-crashes window before the v1.0 tag.
- **Trigger** — Phase 3 layout work complete (so the fuzz surface is
  stable + the engine is feature-complete enough to be worth fuzzing
  at the planned depth).
- **Owner files** — `tests/NetPdf.Fuzz/` (existing project; needs the
  8 harnesses); CI workflow under `.github/workflows/` for the gate.
- **Added** — Phase D PR #18 documented the deferral; carried into
  `PROGRESS.md` § Pending pre-v1.0 work.
- **Removal condition** — All 8 harnesses ship, CI gate green, 30-day
  no-new-crashes window observed.

---

## In-progress work — pointer

The scheduled Phase 3 work breakdown (Tasks 11–30 — `TableLayouter`,
`MulticolLayouter`, `FlexLayouter`, `GridLayouter`, `AbsoluteLayouter`,
page-margin boxes, `string()` / `running()` / `counter()`, diagnostics,
W3C runner, corpus end-to-end render, performance tuning, `0.7.0-beta`
tag) lives in
[`docs/phases/phase-3-layout-and-pagination.md`](phases/phase-3-layout-and-pagination.md).
Active state for whichever sub-cycle is in flight lives in
[`PROGRESS.md`](../PROGRESS.md). This file deliberately does **not**
duplicate either — they own the live status; this file owns the
approximation/throw contracts.

## GridLayouter (Tasks 17 + 18) — design pointer

**Active design**:
[`docs/phases/task-17-grid-design.md`](phases/task-17-grid-design.md)
(v2 post-PR-#88 review). 8-cycle plan: cycle 0 = CSS infrastructure
groundwork; cycles 1-7 = layouter feature progression (Hello World
→ fr → intrinsic → minmax/repeat → multi-page → spans → named
areas/dense).

**Known cycle-internal approximations + deferrals** (= will be
documented here in detail as each cycle ships; this entry just
flags the categories):

- **Intrinsic sizing for `auto` / `min-content` / `max-content` tracks**
  (cycle 3) — approximated as item's declared dimension; zero for
  items without explicit width/height. Closes when L19 content
  measurement ships (same blocker as flex `min-width: auto`).
- **Row-spanning items at page breaks** (cycle 5) — atomic-to-row-span
  (= entire item defers if any spanned row would land on a later
  page). Intra-row item splitting is post-v1.
- **Subgrid** (CSS Grid L2 only) — out-of-scope for v1.
- **Masonry** (CSS Grid L3 draft) — out-of-scope for v1.
- **Cell-internal alignment** (`justify-self` / `align-self` on
  grid items) — separate Task that shares the
  `<self-position>` decoder with FlexLayouter.
- **Baseline alignment** in grid cells — blocked on L18.
- **`grid` shorthand parser** — Task 19/20 housekeeping post-cycle-7.

---

## grid-track-sizing-cycle3-narrowed-scope

- **ID** — `grid-track-sizing-cycle3-narrowed-scope`
- **Status** — `approximated`. Phase 3 Task 17 cycle 3 + post-PR-#94
  review hardening F4.
- **Behavior** — CSS Grid track sizing (CSS Grid Layout L1 §11) ships
  three track kinds via the shared `GridSizing` service:
  - **Length tracks** — fully spec-correct (resolved in §11.4
    pre-pass).
  - **Flexible (`fr`) tracks** — §11.7 "Find the Size of an fr"
    with the spec-correct `flexFactorSum = max(SUM(factors), 1.0)`
    floor (the floor applies once to the TOTAL, not per-track).
    Under an indefinite block axis fr collapses to zero +
    `LayoutGridFrUnderIndefiniteApproximated001` fires.
  - **Intrinsic (`auto` / `min-content` / `max-content`) tracks** —
    a content-determined cell now CONTENT-measures both axes: ROW
    tracks size to the cell's content block extent at its column
    width (#182), and COLUMN tracks size to the cell's MAX-CONTENT
    inline extent measured unconstrained (grid content-width cycle,
    branch `phase-3-riders-perpage-geometry-inline-img-grid-cols` —
    `GridSizing.ItemOuterContribution` + the `widthMeasurer` closure).
    A cell with a DECLARED dimension still uses it (the L19
    declared-dimension contribution + border + padding + margin), and
    the contribution is FLOORED at the item's absolute `min-height` /
    `min-width` (grid min-height cycle — CSS Box Sizing §6.1; a %/keyword
    min-* still reads 0, the chicken-and-egg gap).
- **NOT in cycle 3 / still approximated** — explicitly deferred so the
  narrowed scope doesn't drift:
  - **Spec-strict §11.5 min-content vs max-content distinction** — the
    content measurement above reports MAX-content for both axes;
    `min-content` / `fit-content` / the available-width fit are
    approximated by max-content (same L19 simplification). A spanning
    item now SUBTRACTS the fixed spanned tracks before distributing
    (grid spanning-item distribution cycle, see
    `grid-spanning-item-intrinsic-distribution-deferral`), but splits the
    remainder EQUALLY across intrinsic tracks, not proportional to headroom.
  - **§11.6 Maximize step** — the post-fr-resolution pass that
    grows base sizes up to growth limits when the grid has free
    space + no fr tracks consumed it. Cycle 4 picks this up.
  - **Auto-track stretch** — distributing leftover container space
    across `auto` tracks per `align-content` / `justify-content`
    `stretch`. Separate sub-task (CSS Box Alignment L3 §6) that
    shares its `<content-distribution>` decoder with the existing
    FlexLayouter path.
  - **`box-sizing: border-box`** — `GridSizing.ItemOuterContribution`
    currently always treats declared width/height as content-box +
    adds chrome. Honoring `box-sizing: border-box` (subtract
    border + padding from the declared dimension before adding to
    track sum) is part of the broader box-sizing pass that touches
    BlockLayouter + FlexLayouter symmetrically.
  - **Percentage track / item dimensions resolved against the grid's
    indefinite axis** — the cycle-3 path treats percentages as 0 in
    the indefinite case (the standard CSS Sizing L3 rule). Definite-
    axis percentages already resolve through the existing computed-
    value path.
- **Missing** — the five bullets above; plus the cycle-4+ track
  kinds (`minmax()` / `fit-content()` / `repeat(integer)`) that
  cycle 4 will add.
- **Added** — Phase 3 Task 17 cycle 3 initial ship (intrinsic via
  L19 approximation); cycle 3 post-PR-#94 review hardening F4
  (= explicit narrowed-scope enumeration so reviewers + future me
  don't read the cycle-3 XML doc as "intrinsic tracks fully shipped").
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — shared sizing
    service (Length + Fr + intrinsic via L19); single source of
    truth for both pre-measure (BlockLayouter) and emit
    (GridLayouter).
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` — thin emission
    wrapper; XML doc explicitly enumerates the narrowed scope.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `PreMeasureGridRowExtent` calls `GridSizing.Resolve` with
    `emit: null` so auto-height wrappers reserve intrinsic-row
    space without double-firing diagnostics.
- **Trigger** — cycle 4 (this Task) ships `minmax()` / `fit-content()`
  / `repeat(integer)` + activates the iterative §11.7 fr-removal
  step. Cycle 5+ ships multi-page row split. The L19 deferral
  (true intrinsic content measurement) is the engine-wide blocker
  that closes the L19 approximation here + in FlexLayouter
  simultaneously.
- **Removal condition** — each bullet under "NOT in cycle 3"
  closes individually as the corresponding cycle/task ships. The
  whole entry retires when L19 + §11.6 Maximize + auto-track
  stretch + `box-sizing: border-box` + indefinite-axis percentage
  resolution are all in place; at that point the GridSizing
  service is the spec-complete §11 implementation and this
  enumeration becomes legacy documentation.

---

## grid-maximize-extra-space-receiver-deferred

- **ID** — `grid-maximize-extra-space-receiver-deferred`
- **Status** — `approximated`. Phase 3 Task 17 cycle 4 + post-PR-#95
  review H4.
- **Behavior** — CSS Grid §11.5.1 "Distribute Extra Space" step 3
  says: after finite-growth-limit tracks freeze with leftover space
  remaining, distribute the remainder to "extra-space-receiver"
  tracks (intrinsic max-content tracks first, then fr tracks).
  Cycle 4's `MaximizeTracks` skips tracks with infinite GrowthLimit
  entirely + leaves any post-freeze leftover unallocated.
- **Missing** — the §11.5.1 step 3 second-pass distribution. When
  finite-limit tracks freeze with free space remaining + at least
  one infinite-growth-limit non-fr track exists, that leftover
  should grow the infinite-growth tracks beyond their nominal
  base.
- **Trigger** — a future cycle that ships full §11.5.1 + §11.6
  spec compliance (probably alongside the auto-track stretch
  feature, since both require the same "distribute leftover"
  infrastructure).
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` —
    `MaximizeTracks` second-pass distribution.
- **Practical impact** — small because intrinsic tracks WITH
  placed items get their GrowthLimit set to the contribution
  (= finite) by `ResolveIntrinsicTracks`, so they participate in
  the normal Maximize pass. The gap only manifests for degenerate
  empty-track-with-no-items cases (= the leftover just stays
  unused).
- **Added** — Phase 3 Task 17 cycle 4 + post-PR-#95 review H4.
- **Removal condition** — second-pass distribution implemented +
  validated against a multi-track test where infinite-growth
  tracks correctly absorb post-freeze leftover.

---

## grid-box-sizing-border-box-deferred

- **ID** — `grid-box-sizing-border-box-deferred`
- **Status** — `approximated` (the cross-cutting audit's CORE is COMPLETE: GRID + BLOCK + TABLE + **FLEX**
  all honor `box-sizing` on their declared sizes via the shared `BoxSizingHelper`; the flex box-sizing /
  content-inset cycle closed the last layouter). Only minor residual APPROXIMATIONS remain (below).
- **Behavior** — the shared `BoxSizingHelper.DeclaredToBorderBox(style, declared, chrome)` maps a declared
  size to the used BORDER box honoring `box-sizing` (CSS Basic UI 4 §10): `border-box` → the declared size
  IS the border box (floored at the chrome); `content-box` (initial) → declared + chrome (byte-identical
  to the prior `declared + chrome`). Consumers: `GridSizing.ItemOuterContribution`'s `ItemBorderBoxExtent`
  (grid box-sizing cycle); `BlockLayouter`'s `DeclaredWidthToBorderBox` (#165, now delegating) + the
  block/float explicit-HEIGHT border-box-block-size (box-sizing cycle); `TableLayouter.ReadColumnWidthPx`
  via the new `ColumnBorderBoxWidth` (a cell's declared width feeds the column via its border box); and the
  FLEX item main/cross readers — `ComputedStyleLayoutExtensions.ResolveFlexItemHypotheticalMainSize` +
  `ResolveFlexItemMinMaxMainSize` (main), the emission cross-size read + `FlexLinePacker.CrossBorderBoxSize`
  (cross). The flex emission ALSO now insets a BLOCK-child item's content to the item's content box (=
  border-box origin + the new `InlineStartBorderPaddingPx`/`BlockStartBorderPaddingPx`); an INLINE-ONLY-root
  item's content fragment is left at the border-box origin (the nested `BufferingMeasureSink` flags it via
  `ContainsDecorationOwnerFragment` — `TextPainter` insets its glyphs + its measured extent already folds in
  the item's chrome, so re-insetting / re-adding chrome would double-count).
- **Missing (residual flex approximations only)** —
  - **Percentage padding** reads 0 in the flex chrome (`InlineBorderPaddingPx` uses `ReadLengthPxOrZero`,
    matching the row-flex pre-measure convention) — so a flex item with a `%` padding under-counts chrome.
  - The content inset uses the **LTR horizontal-tb** physical mapping (inline-start = left, block-start =
    top), consistent with the rest of the flex emission's writing-mode / RTL approximation.
  - The flex MAIN-axis flex (grow/shrink) distributes in **border-box space** (the hypothetical + the
    resolved size are border boxes) rather than the spec's content-box-size + outer-margin model — an
    approximation consistent with the engine's border-box-throughout convention; visible only when an item
    with non-zero chrome both has a definite basis AND flexes.
  - A `%` width/height/min still reads 0 in the GRID intrinsic contribution (the chicken-and-egg gap), so
    box-sizing on a PERCENTAGE grid-item size is moot there.
- **Trigger** — a flex item with `%` padding, RTL/vertical writing mode, or a chrome'd flexing item where
  the border-box-space distribution visibly diverges from the spec.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/BoxSizingHelper.cs` — the shared declared→border-box mapping (box-sizing cycle).
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — `ItemOuterContribution` (grid's richer extent).
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — width (#165) + height border-box-block-size + the two
    flex pre-measures (`PreMeasureFlexCrossExtent` / `PreMeasureFlexMainExtent`).
  - `src/NetPdf.Layout/Layouters/TableLayouter.cs` — `ColumnBorderBoxWidth`.
  - `src/NetPdf.Layout/Layouters/FlexLayouter.cs` — emission cross-size + content inset (block-child only).
  - `src/NetPdf.Layout/Layouters/FlexLinePacker.cs` — `CrossBorderBoxSize` (wrapping line cross extent).
  - `src/NetPdf.Layout/Layouters/ComputedStyleLayoutExtensions.cs` — the chrome helpers + the flex-item
    hypothetical / min-max box-sizing.
  - `src/NetPdf.Layout/Layouters/BufferingMeasureSink.cs` — `ContainsDecorationOwnerFragment`.
- **Practical impact** — RESOLVED for the audit's core: items using `box-sizing: border-box` (the modern
  norm for reset stylesheets like Bootstrap) now size correctly across all four layouters. The residuals
  above are niche.
- **Added** — Phase 3 Task 17 cycle 3 (initial known-gap noted
  in `ItemOuterContribution` xmldoc); post-PR-#95 review H6
  formalized as a deferral entry; GRID side resolved in the grid box-sizing cycle; FLEX (the last layouter)
  + the content inset resolved in the flex box-sizing / content-inset cycle.
- **Removal condition** — the residual flex approximations (percentage-padding chrome, writing-mode/RTL
  inset, border-box-space main flex) + the grid `%`-size chicken-and-egg gap are all closed.

---

## grid-sizing-perf-optimizations-deferred

- **ID** — `grid-sizing-perf-optimizations-deferred`
- **Status** — `approximated`. Phase 3 Task 17 cycle 4 + post-PR-#95
  review P2 + P4 + P5.
- **Behavior** — `GridSizing` has known hot-path allocation +
  computation patterns that are functionally correct but leave
  perf on the table:
  - **P2 — Per-Resolve allocations**: `new List<TrackListItem>` in
    ExpandTrackList; `new List<TrackSizingInfo>` ×2; `new
    SizingContext`; `new TrackListNamedLine` per repeat iteration.
    Cycle 4 hardening landed `stackalloc`-based frozen arrays +
    dropped dead `kindsOut` lists; remaining allocations are
    candidates for `ArrayPool<T>` rental.
  - **P4 — `ItemOuterContribution` repeats 7 ComputedStyle reads
    per item per intrinsic-track-resolution**. For a 50×50 grid
    with 100 items + every track intrinsic, that's 35,000+
    dictionary lookups per axis. Per-item caching of
    `(width, height, chromeWidth, chromeHeight, marginH, marginV)`
    on `PlacedItem` would eliminate the redundant reads.
  - **P5 — O(N×M) inner loop in `ResolveIntrinsicTracks`**: for
    each track, scan all items. Acceptable for typical grids
    (small N, small M); worst-case
    `repeat(10000, auto)` with 10000 items → 1e8 operations.
    An inverted index `(axisIndex → IList<PlacedItem>)` built
    once before the loop drops this to O(N+M).
- **Missing** — ArrayPool wiring; per-PlacedItem style cache;
  per-axis inverted-index for placed items.
- **Trigger** — the dedicated grid perf-tuning task that's part
  of Phase 3's general performance gate (3-page invoice ≤ 200ms
  p50 / 20-page report ≤ 1.5s p50). If those gates start
  regressing on grid-heavy fixtures, prioritize then.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — all three
    optimization sites.
- **Added** — Phase 3 Task 17 cycle 4 + post-PR-#95 review P2 +
  P4 + P5.
- **Removal condition** — perf gates remain green when grid-heavy
  fixtures are added to the bench suite.

---

## grid-sizing-architecture-followups-deferred

- **ID** — `grid-sizing-architecture-followups-deferred`
- **Status** — `not-started`. Phase 3 Task 17 cycle 4 + post-PR-#95
  review Q2 + Q4 + Q5.
- **Behavior** — `GridSizing.cs` is 1300+ lines mixing repeat
  expansion, track classification, fr distribution, intrinsic
  resolution, Maximize, item placement, and diagnostic emission
  in one static class. Functionally correct + AOT-clean but
  could be split for clarity:
  - **Q2** — `ClassifyEntry` is a closed-switch on track kind.
    A `ITrackSizingStrategy` polymorphic dispatch was considered
    but rejected because the closed-set discriminated-union +
    switch is faster (no v-table) + more AOT-friendly. Trade-off
    is documented in the file's xmldoc; entry exists so a future
    reviewer doesn't re-litigate the decision.
  - **Q4** — Split GridSizing.cs into:
    `GridTrackExpander.cs` (ExpandTrackList + truncation),
    `GridTrackClassifier.cs` (ClassifyEntry + ClassifyMinMax),
    `GridTrackSizingPipeline.cs` (fr + intrinsic + Maximize),
    `GridItemPlacer.cs` (RunPlacement + helpers). `GridSizing`
    becomes the orchestration entry-point only.
  - **Q5** — `ItemOuterContribution` has duplicated row/col
    branches that could fold into a shared
    `AxisProperties record struct(PropertyId Size, PropertyId
    Border1, PropertyId Pad1, ...)`. Drops ~25 lines.
- **Missing** — each refactor listed above.
- **Trigger** — when adding cycles 5-7 (multi-page split, spans,
  named areas) becomes painful due to the file's size + mixed
  responsibilities. OR when a new contributor's onboarding pain
  surfaces the issue.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs`.
- **Added** — Phase 3 Task 17 cycle 4 + post-PR-#95 review Q2 +
  Q4 + Q5.
- **Removal condition** — when the refactors land, the file
  splits exist, and the tests still pass with no behavioral
  change.

---

## grid-break-resolver-integration-deferred

- **ID** — `grid-break-resolver-integration-deferred`
- **Status** — `approximated`. Phase 3 Task 17 cycle 5 + post-PR-#96
  review F2 (full IBreakResolver wiring deferred).
- **Behavior** — `GridLayouter.AttemptLayout` accepts
  `IBreakResolver` + `LayoutAttemptStrategy` parameters as
  required by the layouter interface, but cycle 5 hardening only
  consults `LayoutAttemptStrategy` (= `LastResort` vs everything-
  else gate for force-overflow per PR-#96 F1+F2 partial). The
  resolver itself is not consulted: grid row boundaries are NOT
  registered as `BreakOpportunity` values; author break policy
  (`break-before` / `break-after` / `break-inside` on grid rows
  + items) is not honored; the cost-model optimizer can't
  influence grid-row break decisions.
- **Missing** —
  - Model grid row boundaries as `BreakOpportunity` values
    registered with `IBreakResolver`.
  - Define grid rewind behavior explicitly (= what does the
    resolver do when `break-inside: avoid` on a row fires inside
    a grid? Does it walk back to the prior page-eligible row?).
  - Honor `break-before` / `break-after` on grid items + auto-
    break opportunities between rows per CSS Fragmentation L3 §5.
  - Restrict the §4.4 progress-rule overflow to `LastResort`
    behavior in the resolver itself (not just the layouter's
    strategy parameter).
- **Trigger** — alongside CSS `break-before` / `break-after` /
  `break-inside` support for grid rows (= the property values
  must first parse + resolve through the cascade; cycle 5+
  hardening can then wire them into the resolver).
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` —
    `ComputePaginatedRowRange` would consult the resolver per row.
  - `src/NetPdf.Paginate/IBreakResolver.cs` — may need a new
    `RegisterGridRowBoundary` API or extension of existing
    `RegisterBreakOpportunity`.
  - `src/NetPdf.Css/properties.json` — `break-before` /
    `break-after` / `break-inside` if not already registered.
- **Practical impact** — cycle 5 ships row-by-row pagination
  driven solely by geometry. Authors who use CSS break properties
  on grid items today (= rare in invoice/report use cases that
  drive v1) see no effect. The cycle-5 LastResort gating (F1+F2
  partial) is sufficient for the cycle 5b dispatch flow to be
  safe; the FULL resolver integration is a separate cycle's work.
- **Added** — Phase 3 Task 17 cycle 5 + post-PR-#96 review F2.
- **Removal condition** — `BreakOpportunity` integration ships
  + the resolver controls grid row breaks + CSS break-*
  properties are honored on grid rows / items.

---

## grid-fragment-plan-shared-sizing-deferral

- **ID** — `grid-fragment-plan-shared-sizing-deferral`
- **Status** — `approximated`. Phase 3 Task 17 cycle 5c.2b post-
  PR-#100 review P2; **partially addressed in Task 18** (the
  cross-page row-extent memo landed; the full per-attempt plan
  stays deferred — see below).
- **Behavior** — auto-height paginatable grids run
  `GridSizing.Resolve` up to three times per attempted fragment:
  (1) in `PreMeasureGridRowExtent` to grow the wrapper to
  natural extent; (2) in F1's `PreMeasureGridRowExtentAt`
  probe (when no incoming cache present); (3) inside
  `GridLayouter.AttemptLayout` for the actual dispatch.
- **Task 18 finding (corrects the original premise)** — the
  three Resolves are NOT redundant duplicates; they take
  GENUINELY DIFFERENT inputs, so a naive "compute once, share
  across all three" would be **incorrect**: (1) uses an
  INDEFINITE block budget (`contentBlockSize: 1`) to compute
  the auto-height grid's natural extent — under indefinite
  block, `fr` rows collapse (CSS Grid §11.5), whereas (3) uses
  the DEFINITE wrapper extent where `fr` rows expand to fill;
  the two produce different row sizes for `fr` grids (the
  chicken-and-egg that forces site 1 to be separate). (2) the
  probe passes NO content/width measurers, so its content-row
  sizes differ from (3)'s. The Length-only fixtures that "don't
  surface it" are exactly the case where indefinite≡definite +
  measurers don't matter. Additionally, the EXPENSIVE work —
  per-cell content SHAPING — is ALREADY shared across all three
  sites via `GridMeasurementCache` (the `MeasurePassCount`
  regression test pins it); the residual redundancy is only the
  §11/§8.5 ARITHMETIC, which is comparatively cheap.
- **Task 18 (what landed)** — `PreMeasureGridRowExtent`'s
  natural row extent is page-INVARIANT (indefinite block budget;
  fixed inline + page budget), and a multi-page grid re-grows
  its wrapper on every page. That site-1 §11 pass is now
  memoized on `GridMeasurementCache.RowExtentSum` keyed by
  (grid box, inline size, measure budget), so resume pages +
  rewind retries skip it entirely (the `RowExtentComputeCount`
  regression test pins "== 1 across a multi-page grid").
  Byte-identical (deterministic Resolve).
- **Practical impact** — the first-page 3× remains (three
  genuinely-different resolves; not safely collapsible), but
  the expensive shaping is shared + the cross-page site-1
  re-resolve is now elided.
- **Missing** — collapsing the first-page site-2 (probe) and
  site-3 (dispatch) resolves where they DO align (Length-only
  grids, or by giving the probe the dispatch's measurers — but
  that shifts content-grid pagination decisions, so it needs a
  reftest sweep); a full per-attempt `GridFragmentPlan` is only
  correct for the definite-block sites (2+3), NOT site 1.
- **Trigger** — when a benchmark on a large multi-page grid
  shows measurable CPU regression vs cycle 5b atomic dispatch.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridMeasurementCache.cs` —
    `RowExtentSum` memo + `RowExtentComputeCount` (Task 18).
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `PreMeasureGridRowExtent` consults the memo;
    `PreMeasureGridRowExtentAt` + `DispatchGridInner` would
    thread a definite-block plan for the 2+3 collapse.
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` —
    `ConfigureEmission` accepts a precomputed plan in lieu of
    running its own `Resolve`.
- **Added** — Phase 3 Task 17 cycle 5c.2b + post-PR-#100 review
  P2; partially addressed Phase 3 Task 18.
- **Removal condition** — the site-2+3 definite-block collapse
  lands AND a benchmark shows ≤ 1× CPU vs cycle 5b atomic
  dispatch for paginatable-grid fixtures.

---

## recursive-block-continuation-consumed-extent-accounting-deferral

- **ID** — `recursive-block-continuation-consumed-extent-accounting-deferral`
- **Status** — `not-started`. Phase 3 Task 17 cycle 5c.2d
  post-PR-#102 review P1/P2#2.
- **Behavior** — when a recursive grid (or flex / multicol /
  table) inside <see cref="EmitBlockSubtreeRecursive"/> emits
  some rows + returns `PageComplete(NestedContinuation)`, the
  recursion wraps the result into
  `BlockContinuation(ResumeAtChild=childIdx,
  ConsumedBlockSize=0, LayouterState=NestedContinuation)`
  before returning up the chain. The outer
  <see cref="AttemptLayout"/> then wraps the chain into
  PageComplete using the OUTER's UsedBlockSize delta — but
  the recursive grid's F2-resized emission only updated
  `childCursor` (a local), NOT
  `fragmentainer.UsedBlockSize`. Result: continuation
  accounting reports a lower ConsumedBlockSize than actually
  committed for fragments where pagination happened deep in
  the recursion tree.
- **Practical impact** — `BlockContinuation.ConsumedBlockSize`
  is documented as cumulative-across-pages, but for
  recursive-paginated grids it under-reports the page's
  committed extent. Consumers that rely on this value for
  cost/extent metrics, ancestor continuation semantics, or
  following-sibling placement on resumed pages could see
  geometry inconsistency. The visible fragment emission is
  correct (= rows render at the right offsets + F2 resizes
  the wrapper); only the continuation-chain's reported
  consumed extent diverges from the actual committed extent.
- **Pre-existing scope** — this pattern exists today for flex
  + multicol recursive PageComplete propagation too; not
  unique to grid. The cycle-5c.2d wiring surfaced the issue
  via the grid path but the fix needs to be uniform across
  all nested-container layouters.
- **Missing** — recursion's return type extended from
  `LayoutContinuation?` to a typed result carrying
  `(Continuation, CommittedBlockExtent)`. Outer
  `AttemptLayout` reads CommittedBlockExtent to advance
  `fragmentainer.UsedBlockSize` exactly once before
  wrapping the PageComplete. Mirrors the
  <c>GridLayouter.LastEmittedBlockExtent</c> producer +
  consumer contract from cycle 5c.1 but at the recursion
  boundary.
- **Trigger** — when downstream consumers of
  `BlockContinuation.ConsumedBlockSize` (= cost model
  refinements, multi-document concatenation, or any future
  feature reading cumulative consumed extent) need accurate
  recursive accounting. Production renders are visually
  correct today.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `EmitBlockSubtreeRecursive` return type + the 3 recursive
    grid / flex / multicol PageComplete propagation sites.
  - `src/NetPdf.Paginate/LayoutContinuation.cs` —
    BlockContinuation.ConsumedBlockSize semantics doc update.
- **Added** — Phase 3 Task 17 cycle 5c.2d post-PR-#102 review
  P1/P2#2.
- **Removal condition** — recursion return type carries
  committed-extent; outer AttemptLayout advances
  fragmentainer.UsedBlockSize from the recursion's committed
  extent (NOT from MeasureSubtreeVisualBlockExtent's natural
  extent) when a recursive paginated emit happened;
  end-to-end tests assert ConsumedBlockSize matches emitted
  extent for `<body><sibling><grid>...</grid></body>` shapes.

---

## grid-spanning-item-intrinsic-distribution-deferral

- **ID** — `grid-spanning-item-intrinsic-distribution-deferral`
- **Status** — `approximated` (FURTHER improved — post-PR-#185 review F1). The §11.5.1 "subtract the
  affected size of EVERY spanned track" step + order-independent planned increases now ship; the
  remaining gap is the PROPORTIONAL (vs equal) split of the remainder + growth-limit freezing + the
  separate max-content (growth-limit) spanning pass.
- **Behavior** — A spanning item (= `grid-row: span N` or `grid-row: A / B` with `B - A > 1`) is resolved
  in `GridSizing.DistributeSpanningItems` (post-PR-#185 review F1): `ResolveIntrinsicTracks` runs a
  NON-spanning sub-pass first (each single-track item sizes its track), then this helper distributes each
  spanning item's `extra = max(0, itemContribution − Σ current base size of ALL spanned tracks)` EQUALLY
  across the spanned BASE-GROWING tracks (auto / min-content / max-content / fit-content / minmax with an
  intrinsic min — `TrackBaseGrowsFromIntrinsicMin`). The subtraction (`SpannedTrackCurrentBase`) counts
  EVERY spanned track — including an intrinsic one a non-spanning item already sized AND a
  `minmax(<len>, auto)` track's fixed min — so a track already covering its share isn't re-grown (the
  first cut subtracted only NON-intrinsic tracks → double-counted an already-sized intrinsic track). A
  `minmax(<fixed>, intrinsic)` track keeps its fixed-min base (only its growth limit grows, §11.5 step 4),
  so it is subtracted but never a distribution target. Items are grouped by span count ASCENDING and each
  track's planned increase is the MAX over the items in its group, committed AFTER the group, so the
  result is ORDER-INDEPENDENT (§11.5.1).
- **Missing** — Per CSS Grid L1 §11.5.1, the remainder should be distributed PROPORTIONAL to each
  intrinsic track's headroom (its max-content − base size) with per-track growth-limit FREEZING + a
  "distribute space beyond limits" step, not split equally; and the spec runs a SEPARATE max-content
  (growth-limit) spanning pass (a spanning item currently grows only base sizes, not the growth limits of
  intrinsic-max-only tracks). The subtract-all-tracks step, the negative-remainder floor (no growth), and
  order-independence now match the spec; only the proportional / growth-limit-freezing / max-content split
  remains an approximation.
- **Trigger** — corpus invoice / report uses `grid-row: span N`
  with mixed-kind tracks (some length / fr, some auto /
  min-content) AND the equal-share approximation produces
  visible mis-sizing (e.g., the spanning item over-grows the
  intrinsic tracks because the per-track equal share exceeds
  what the spec would distribute after subtracting definite-
  track contributions).
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` —
    `DistributeSpanningItems` / `SpannedTrackCurrentBase` /
    `TrackBaseGrowsFromIntrinsicMin` (post-PR-#185 review F1)
    extended to add per-track growth-limit freezing +
    proportional-to-headroom distribution + the separate
    max-content spanning pass.
- **Added** — Phase 3 Task 18 cycle 6a; subtract-all-tracks +
  order-independence in the post-PR-#185 review (this branch).
- **Removal condition** — `DistributeSpanningItems` implements
  the §11.5.1 proportional-to-headroom split with growth-limit
  freezing + a test pins a case where the spanned intrinsic
  tracks have DIFFERENT headrooms (e.g.,
  `grid-template-columns: minmax(0, auto) minmax(0, 200px)`
  with a span-2 item of intrinsic 300px → spec distributes
  proportional to each track's max-content headroom, the equal
  split gives each 150 instead).

---
## grid-implicit-named-area-and-occurrence-syntax-deferral

- **ID** — `grid-implicit-named-area-and-occurrence-syntax-deferral`
- **Status** — `approximated` (NARROWED). Phase 3 Task 18 cycle 7b + the
  residual-long-tail batch (occurrence syntax + end-edge span-by-name +
  named-line-pair placement) + the PR #215 review (§8.3 forward implicit-line
  assumption, negative-start normalization, `(name, line)` dedup); the
  residuals below remain.
- **Behavior** — `GridSizing.ReadPlacement` resolves `<custom-ident>`
  references via the per-axis named-line occurrence map (`BuildNamedLineMap`:
  name → sorted occurrence line numbers, including the implicit `<area>-start`
  / `<area>-end` lines from `grid-template-areas`). Now SHIPPED (this batch):
  - **`<integer> <custom-ident>`** (e.g. `grid-row-start: foo 2`) — the Nth
    line literally named `foo` (`ResolveNamedLineOccurrence`; positive counts
    1-based from the first, negative from the last). Resolved on both the
    start and end edges.
  - **`span <custom-ident>` / `span <custom-ident> <integer>`** (e.g.
    `grid-row-end: span foo`) — on the END edge with a definite start, spans
    to the Nth `foo` line strictly after the start (`ResolveSpanToNamedLine`).
  - **§8.3 implicit-line assumption** (PR #215 review [P1]) — when fewer than
    N explicit `foo` lines exist, a POSITIVE occurrence / forward span resolves
    through the implicit lines past the explicit grid's end edge (each assumed
    named `foo`), capped at `MaxImplicitTracksPerAxis`. So `foo 2` with one
    explicit `foo`, and `1 / span foo 2` with too few `foo` lines, extend the
    grid with implicit tracks instead of falling back to auto. The negative
    integer start is normalized against the explicit-grid track count BEFORE
    the named-end span math; the `(name, line)` set is deduplicated per §8.1.
  - **Named-line-pair placement** — `[foo-start] … [foo-end]` line pairs
    place a `grid-row: foo` / `grid-area: foo` item at the foo region via the
    line-name lookups (`<ident>-start` / `<ident>-end`), even with no
    `grid-template-areas` entry (the §8.4 implicit named area's PLACEMENT
    effect, achieved through the line map).
- **Missing** —
  - **`span <custom-ident>` on the START edge / an auto start** (e.g.
    `grid-row-start: span foo`, or `grid-row: auto / span foo`) — the span
    count depends on where auto-placement lands the opposite edge, so it
    needs the auto-placement span algorithm; still falls back to auto with
    `LAYOUT-GRID-PLACEMENT-APPROXIMATED-001`.
  - **Negative-occurrence start-side implicit fill** (e.g. `foo -3` with too
    few `foo` lines) — the reverse (negative) direction's implicit lines (at
    0, −1, …) are not synthesised; an underflowing negative occurrence still
    falls back to auto. Only the forward (positive) implicit fill shipped.
  - **Explicit implicit-area `GridAreaRect` registration** — the line-pair
    placement works via the line map (above), but a `GridAreaRect` for the
    derived area is not registered in `GridTemplateAreas.NameToRect`; anything
    that reads `NameToRect` directly (rather than via the line lookups) won't
    see the implicit area. Functionally redundant for placement today.
- **Trigger** — `grid-row-start: span foo` / auto-start span-by-name in the
  corpus, OR a consumer that reads `NameToRect` for an implicit (line-pair)
  area.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — `ReadPlacement` (start-edge
    span-by-name needs the auto-placement span count); `BuildNamedLineMap` /
    `Resolve` (= would intersect the two axes' line maps to register a
    `GridAreaRect` for matching `*-start` / `*-end` pairs).
- **Added** — Phase 3 Task 18 cycle 7b (post-PR-#106 review); narrowed in the
  residual long-tail batch when occurrence + end-span + line-pair placement
  shipped.
- **Removal condition** — `span <custom-ident>` resolves on the start / auto
  edge too (via the auto-placement span algorithm) AND the negative-occurrence
  start-side implicit fill is synthesised AND the implicit-area `GridAreaRect`
  is registered in `NameToRect` for `*-start` / `*-end` line pairs.
---

## abspos-cycle-1-explicit-only

- **ID** — `abspos-cycle-1-explicit-only`
- **Status** — `approximated`. Phase 3 Task 19 cycle 1 ships the
  explicit-offset MVP; the rest of CSS Positioned Layout L3 §6 is
  cycle 2+.
- **Behavior** — `position: absolute` boxes are removed from normal
  flow (don't advance the cursor, don't break margin adjacency) +
  placed by `AbsoluteLayouter.ResolvePlacement` against the
  establishing block's CONTENT box. Cycle 1 resolves ONLY explicit
  pixel `top` + `left` + `width` + `height`. Any box using a deferred
  feature is DROPPED (no fragment) with
  `LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001` rather than mis-placed.
- **Containing block (cycle 1)** — the establishing `BlockLayouter`'s
  content area = the fragmentainer content box
  `(0, 0, contentInlineSize, blockSize)`. For the top-level layouter
  this coincides with the initial containing block AND with a
  positioned root's content box. Abspos descendants at ANY depth are
  collected by the top-level post-flow pass + anchored to this ICB.
- **Missing** —
  - ~~**Nearest-positioned-ancestor CB + ancestor walk**~~ — SHIPPED
    in cycle 2a (+ post-PR-#113 review). An abspos box inside a
    `position: relative` (or any non-`static`) ancestor now anchors to
    that ancestor's PADDING box (`Box.Parent` walk + per-box geometry
    recorded during in-flow emit at the block-flow + recursive +
    forced-overflow emit sites; padding box = recorded border box
    inset by border widths). ICB remains the fallback when there's NO
    positioned ancestor. Per PR-#113 review P1#1: when a positioned
    ancestor IS found but its geometry wasn't recorded, the box is
    DROPPED + diagnosed (`LAYOUT-ABSOLUTE-FEATURE-UNSUPPORTED-001`)
    rather than misplaced at the ICB.
    Per post-PR-#114 review P2#4: a positioned GRID ITEM / TABLE CELL /
    abspos box that is itself the root of a nested-BlockLayouter
    formatting context now resolves its OWN abspos descendants against
    that nested layouter's ICB (= the positioned root's content area),
    rather than dropping them — the nested layouter owns + emits them
    exactly once, and the top-level pass stops at that delegation
    boundary (grid items + table cells/captions) so it doesn't
    double-emit. (Flex is NOT a boundary — `FlexLayouter` spawns no
    per-item nested BlockLayouter, so an abspos box inside a flex item's
    content is emitted by the top-level pass; adding flex to the
    boundary would DROP it.)
    Remaining gap: **positioned GRID / FLEX / TABLE containers (the
    WRAPPER) as CB establishers** — a positioned ancestor that sits
    ABOVE a nested layouter's subtree (e.g., a positioned grid container
    with a static item holding the abspos box) still isn't recorded on a
    path the abspos pass can read, so such a box anchors to the ICB or a
    higher recorded ancestor / is dropped + diagnosed. Later cycle.
  - ~~**`position: relative` offset application (to the CB)**~~ —
    SHIPPED in cycle 2b. A `position: relative` ancestor's §9.4.3 shift
    (`left`/`top`, or `-right`/`-bottom`) is now applied to the abspos
    descendant's CB origin. Per post-PR-#114 review P1#2: PERCENTAGE
    relative offsets resolve PER-AXIS — `left`/`right` against the
    ancestor's inline extent, `top`/`bottom` against its BLOCK extent
    (pre-fix both used the inline extent, shifting the block axis along
    the wrong dimension whenever the two differ). (The relative box's
    OWN in-flow fragment is still emitted unshifted — applying relative
    offsets to the relative box's own rendering is a separate
    relative-positioning slice.)
  - ~~**`auto` offset resolution (static position)**~~ — SHIPPED in
    cycle 2b as an APPROXIMATION: `auto` insets resolve to the CB
    content origin (offset 0), exact when the box would have been the
    first in-flow child. True static-flow-position tracking is a later
    refinement.
  - ~~**`right`/`bottom` anchoring + over-constrained resolution**~~ —
    SHIPPED in cycle 2b: the full CSS 2.1 §10.3.7 / §10.6.4 constraint
    solver (right/bottom anchoring, over-constrained "ignore the end
    inset" LTR/ttb rule, auto-margin centering). Per post-PR-#114 review
    P1#1: the auto-margin centering applies the §10.3.7 negative-slack
    rule on the INLINE axis only — an over-constrained over-wide box
    with both inline margins `auto` pins `margin-left` to 0 (LTR) and
    lets `margin-right` absorb the negative slack (stays anchored at
    `left`), rather than splitting the negative slack equally (which
    would shift it left of `left`). The BLOCK axis (§10.6.4) has no such
    clause and still centers even when the margins go negative.
  - ~~**Percentage** `top`/`left`/`width`/`height`~~ — SHIPPED in
    cycle 2b (resolve against the CB inline / block extent).
  - **`auto` width/height (true shrink-to-fit / content height)** —
    cycle 2b approximates an `auto` size NOT pinned by both insets as
    the AVAILABLE extent (CB minus resolved insets + margins + chrome).
    The pinned-both-insets case (fill) is EXACT. True shrink-to-fit
    (inline) + content height (block) need intrinsic-size measurement
    (the speculative-measure machinery TableLayouter uses) — a later
    refinement. Per post-PR-#114 review P2#3: when the end inset
    (`right`/`bottom`) exceeds the CB and the available size goes
    negative, the size clamps to 0 but the END anchor is PRESERVED —
    the box's start offset is recomputed from the end inset (a negative
    offset) instead of being re-pinned to the static position 0.
  - **Padding-box CB** — uses the recorded border box inset by border
    widths = the padding box (correct). [Resolved cycle 2a.]
  - **z-index paint ordering** — paints in source order; no z-index.
  - **Pagination interaction** — cycle 1 emits all abspos boxes on the
    establishing block's FIRST page (the `AttemptLayout` wrapper runs
    the pass once, for `_incomingContinuation is null`, on AllDone OR
    PageComplete — per post-PR-#112 review C2 so multi-page in-flow
    content doesn't drop abspos fragments). Deciding which page an
    abspos box belongs on (e.g., anchored to content that paginates),
    + abspos boxes taller than a page, remain deferred.
  - ~~**`position: fixed`**~~ — Task 20 cycle 1 SHIPPED (out-of-flow,
    page/ICB CB, repeated on every page). See the `fixed-cycle-1`
    deferral below for the cycle-1 scope + its deferred refinements.
- **Trigger** — real corpus documents using positioned overlays /
  badges / watermarks beyond the explicit-offset MVP, OR a
  user-reported case.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/AbsoluteLayouter.cs` — placement math
    (extend to auto/percentage/right/bottom/auto-size).
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `EmitAbsolutelyPositionedChildren` /
    `EmitAbsolutelyPositionedDescendants` (CB = nearest-positioned-
    ancestor padding box via the `Box.Parent` walk; pagination).
  - `src/NetPdf.Layout/Layouters/ComputedStyleLayoutExtensions.cs` —
    `ReadPosition` / `EstablishesAbsoluteContainingBlock`.
- **Added** — Phase 3 Task 19 cycle 1.
- **Removal condition** — the nearest-positioned-ancestor padding-box
  CB resolution lands AND auto/percentage/right/bottom/auto-size are
  resolved per §6 AND a production-pipeline test pins
  `position: absolute` inside `position: relative` anchoring to the
  relative ancestor (not the ICB).

## fixed-cycle-1

- **ID** — `fixed-cycle-1`
- **Status** — approximated (cycle-1 MVP shipped; refinements deferred)
- **Spec** — CSS Positioned Layout L3 §4 / §6; CSS 2.1 §10.
- **Behavior** — `position: fixed` boxes are removed from normal flow
  (via `IsOutOfFlow`, like absolute) and emitted by a separate post-flow
  pass (`BlockLayouter.EmitFixedPositionedChildren`) that runs on EVERY
  page (NOT gated on the incoming continuation) and ONLY on the page-root
  layouter (`_rootBox.Kind == BoxKind.Root`). The containing block is
  ALWAYS the page / initial containing block (the fragmentainer content
  area); placement reuses the §6 `AbsoluteLayouter.ResolvePlacement`
  solver. The page-root layouter is the SOLE owner of fixed emission
  (every nested item / cell / column / abspos-content / fixed-content
  sub-layouter has a non-Root root box, so none of them run a fixed
  pass), so the walk descends into EVERY subtree — in-flow boxes, grid /
  flex / table items, `position: absolute` subtrees, and a fixed box's
  own subtree (post-PR-#115 review P2#1) — emitting only fixed boxes
  (each page-anchored, exactly once); normal content + abspos boxes are
  emitted by their own passes. Out-of-flow children of grid / flex
  containers are excluded from item collection via `IsOutOfFlow`
  (post-PR-#115 review P1), so a fixed direct child of a grid/flex
  container is neither sized nor emitted as an item.
- **Missing** —
  - **Transform/filter/will-change ancestor as CB** — a `transform` (or
    `filter`, `will-change`, `contain: paint`) ancestor captures fixed
    positioning per CSS Transforms L1 §3 → the fixed box's CB becomes
    that ancestor, not the page. Deferred: those properties aren't wired
    into layout yet, so cycle 1 always uses the page (correct until they
    land).
  - **Page-margin-box / `@page` interaction, z-index paint order** — out
    of scope for cycle 1 (z-index is shared with the abspos deferral;
    `@page` margin boxes are Task 21+).
  - **`overflow: hidden` clipping** — cycle 2 emits overflow per
    `overflow: visible` (the default). Honoring `overflow: hidden` /
    `clip` on a fixed box (actually clipping the overflow) is a separate
    `overflow`-property feature, not yet wired.
  - ~~**Fixed-box content overflow (clipped, not paginated)**~~ —
    RESOLVED in cycle 2 (final design per post-PR-#116 review P1): fixed
    content is dispatched with `FragmentainerContext.SuppressBlockPagination`
    — the break resolver returns `Continue` at every opportunity + float
    deferral is skipped, so content lays out in ONE pass and OVERFLOWS the
    box at its natural position (`overflow: visible`). Crucially the inner
    fragmentainer's `BlockSize` stays the box content-area height (NOT an
    inflated budget), so it remains the containing-block extent the §6
    solver uses for descendant percentage / `bottom` resolution — an
    abspos child with `bottom: 0` or `height: 100%` anchors to the box,
    not to an artificial budget. (The initially-proposed inflated-budget
    approach was rejected in review because the §6 solver DOES resolve
    abspos `%`/`bottom` against the CB block extent, unlike normal-flow
    `ReadLengthPxOrZero`.)
  - ~~**Fixed inside an abspos / fixed subtree**~~ — RESOLVED
    post-PR-#115 review P2#1: the root-owned walk now descends into
    `position: absolute` + fixed subtrees, so a fixed box nested inside
    either is page-anchored + emitted exactly once.
  - ~~**Fixed as a direct child of a grid / flex CONTAINER**~~ —
    RESOLVED post-PR-#115 review P1: `GridSizing.IsGridItem` +
    `GetFlexChildrenInOrderSequence` now exclude `IsOutOfFlow` (not just
    abspos). (Tables collect cells by `BoxKind.TableCell`; a fixed child
    is never a cell, so no analogous fix is needed.)
- **Trigger** — corpus documents using fixed headers/footers/watermarks
  with overflowing content, transforms landing, OR a user-reported case.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `EmitFixedPositionedChildren` / `EmitFixedPositionedDescendants` /
    `EmitOneFixedBox`; the `_rootBox.Kind == Root` gate in `AttemptLayout`;
    `DispatchAbsoluteChildContents` (cycle 2 `noPaginate` → suppress +
    `disableGridPagination`); the float-defer gate (~line 1180).
  - `src/NetPdf.Paginate/FragmentainerContext.cs` (`SuppressBlockPagination`)
    + `src/NetPdf.Paginate/BreakResolver.cs` (`ConsiderBreakAt` honors it).
  - `src/NetPdf.Layout/Layouters/ComputedStyleLayoutExtensions.cs` —
    `IsFixedPositioned` / `IsOutOfFlow`.
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` (`IsGridItem`) +
    `GetFlexChildrenInOrderSequence` — out-of-flow item exclusion.
- **Added** — Phase 3 Task 20 cycle 1.
- **Removal condition** — transform-ancestor CB capture lands AND
  `overflow: hidden` clipping is honored AND z-index paint order is
  resolved (shared with abspos). (Fixed-content natural-position overflow
  shipped in cycle 2.)

## layout-to-pdf-pipeline

- **ID** — `layout-to-pdf-pipeline`
- **Status** — approximated (the public `HtmlPdf.Convert` facade renders
  end-to-end — HTML → layout → paint → PDF bytes — painting
  `background-color` fills + `border-*` edges on a single page as of cycle 3;
  TEXT is not yet painted, so it's a partial approximation, not yet the full
  contract)
- **Spec** — N/A (internal integration of the layout + PDF subsystems
  into the public facade).
- **Behavior** — The layout engine (HTML → cascade → box tree →
  `BlockLayouter` → `BoxFragment`s) AND the PDF byte writer
  (`PdfDocument` / `IContentStream`) both work, but the public
  `HtmlPdf.Convert` facade is a stub. Wiring them end-to-end (real text
  documents → PDF bytes) needs a dependency CHAIN, discovered while
  scoping: **CSS font-property resolution → production text shaper →
  `BoxFragment`→PDF paint bridge → facade**. (The layout engine REQUIRES
  an `IShaperResolver`; the shaper needs resolved font properties.)
- **Done — cycle 1 (production text shaper)** —
  `NetPdf.Shaping.HarfBuzzShaperResolver` implements the layout's
  `IShaperResolver` over a real font: resolves a face via `IFontResolver`
  / `SystemFontResolver` for a `ComputedStyle` + returns a cached
  HarfBuzz `HbShaper`, replacing the synthetic test resolvers. It reads
  `font-size` + `font-style` live (forward-compatible), so it honors real
  CSS automatically once the resolvers in TODO 1 land. Post-PR-#117
  review hardening: resolved bytes are validated through
  `FontSafetyValidator` (rejecting garbage / oversized / WOFF / WOFF2)
  BEFORE HarfBuzz, like `FontFace.Load`; the cache + disposal are
  lock-guarded (a shared resolver is safe across parallel layout); and a
  non-synchronous `IFontResolver` FAILS FAST instead of blocking.
- **Done — cycle 2 (background paint bridge + facade end-to-end)** —
  `HtmlPdf.Convert` / `ConvertAsync` / `ConvertDetailed` now render real
  PDF bytes (no longer throw): `Phase2Pipeline` → single-page
  `BlockLayouter.AttemptLayout` → `FragmentPainter` (the new
  `BoxFragment`→`IContentStream` bridge) → `PdfDocument`. The painter
  emits `background-color` fills with the CSS-px→PDF-pt (×0.75) + y-flip
  transform via the new `PdfPage.FillRectangle`; page size → `/MediaBox`.
  Diagnostics from every stage funnel through one public sink (HTML/CSS
  via `PublicDiagnosticsSinkAdapter`, layout via the new
  `PaginateToPublicDiagnosticsAdapter`, facade direct). Output is
  deterministic — text-free content shapes no glyphs, so the system-font
  dependency (TODO 4) does not affect the bytes. **BORDERS were in the
  cycle-2 scope but were DEFERRED on discovery**: `border-*-width`
  (`PropertyType.LineWidth`) isn't resolved by `PropertyResolverDispatch`
  yet (its documented "cycle 2 backlog", the same gap as `font-size`), so
  border edges can't be sized — wiring `LineWidth` is cross-cutting
  (changes border-box sizing + re-pins snapshots), so borders fold into
  TODO 1. The painter's border-edge design (style keyword ids
  none=0/hidden=1/solid=4…, edge-rect math) was removed as dead code
  pending that. **Single page only**: content overflowing page 1 emits
  `PDF-CONTENT-OVERFLOW-TRUNCATED-001` (surfaced, not dropped); the
  multi-page driver is TODO 3. Tests: 7 `FragmentPainter` unit + 5
  `PdfPage.FillRectangle` unit + 8 `HtmlPdf.Convert` integration
  (well-formed PDF / background painted / determinism / MediaBox / async
  parity / page count / overflow diagnostic / blank-text doc).
- **Done — cycle 2 review hardening (PR #118)** — `HtmlPdfOptions.Timeout`
  is honored: the facade wraps the render in a linked
  `CancellationTokenSource` (non-positive → immediate; timeout →
  `TimeoutException`; caller cancel → `OperationCanceledException`).
  `PrintBackgrounds: false` skips background painting. Partial-alpha
  backgrounds (0 < α < 255) paint fully opaque + emit
  `PAINT-BACKGROUND-ALPHA-APPROXIMATED-001` — proper PDF constant-alpha
  compositing (ExtGState `/ca`) is a follow-up. The overflow diagnostic
  also fires from a post-layout fragment-bounds check (inline overflow /
  forced-overflow AllDone / negative offsets), not just the `PageComplete`
  continuation path. The HarfBuzz resolver trust boundary
  (`FontSafetyValidator` before HarfBuzz + synchronous-completion
  fail-fast) was already in place from cycle 1 — unchanged, now reachable
  through the facade.
- **Done — cycle 3 (border painting)** — `border-*-width`
  (`PropertyType.LineWidth`: thin/medium/thick → 1/3/5px + `<length>`) now
  resolves in the cascade via the new `LineWidthResolver` (+ the line-width
  properties joined `NonNegativeProperties`). The CSS B&B 3 §4.3 used-value
  rule — width 0 when `border-style` is none/hidden — is applied as a style
  gate in `ComputedStyleLayoutExtensions.ReadLengthPxOrZero` (the single
  reader every layout border-width site funnels through), so unbordered boxes
  (default `border-style: none`) reserve no border space and resolving the
  initial `medium` (3px) doesn't grow every box. `FragmentPainter` paints the
  four border edges (full-span overlapping rects); non-`solid` painted styles
  render as solid + emit `PAINT-BORDER-STYLE-APPROXIMATED-001`. Ripple was
  contained to 12 box-model unit tests that set synthetic `border-*-width`
  without a style (now declare `solid`) + the cycle-2 parity test (LineWidth
  moved to the resolved set); LayoutSnapshots + RealDocuments unaffected.
  Tests: `LineWidthResolverTests`, `BorderWidthStyleGateTests`, + facade
  border integration. `column-rule-width` also resolves (same type) but is
  unconsumed by layout, so it's a harmless unused slot.
- **Done — cycle 4 (font-property resolution)** — `font-size` (absolute-size
  keywords + absolute lengths in the dispatch; `em`/`%`/`larger`/`smaller`
  resolved against the parent in the box-builder walk via `FontSizeResolver` +
  the new `ResolveDeferredFontProperties` step), `font-weight` (keyword/number
  → integer; `bolder`/`lighter` against the parent) and `font-family` (a
  side-table `FontFamilyList`, mirroring grid `TrackList`) now resolve;
  `HarfBuzzShaperResolver` reads the resolved family + weight. **Ripple was
  tiny:** `medium`→16px keeps default-font-size content unchanged, so only 4
  expectation tests (which asserted these types were `UnsupportedUnvalidated`)
  needed updating — LayoutSnapshots (30) + RealDocuments (97) + all
  text-measurement geometry were UNAFFECTED. Tests: `FontSizeResolverTests`,
  `FontWeightResolverTests`, `FontFamilyListResolverTests`,
  `FontPropertyResolutionTests` (em/%/bolder against the parent through the
  real pipeline). DEFERRED follow-ups: `rem`/`rlh` + viewport `font-size`
  (need the root font-size threaded through the walk) and general font-relative
  lengths on non-font properties (`padding:1em`, `width:10em`) — both still
  return `Deferred` (= 0 / 16px default) as before.
- **Post-PR-#120 review — deferred follow-ups** (Roland's review of cycle 4; the
  inherited-font-family P1 + the pseudo/marker/first-line/`br` resolution P2 were
  fixed IN the PR, `795f544`). **DONE** — all five fixed in the cycle-4 review
  follow-ups (PR #121):
  - **(P1) `font-size: 0`** — FIXED via zero-advance shaping. `HbShaper` now accepts
    `fontSizePx == 0` (CSS Fonts 4 allows `[0, ∞]`); every advance/offset converts to
    0, so a `font-size: 0` run shapes to invisible, zero-width glyphs.
    `HarfBuzzShaperResolver` no longer snaps 0 → 16px (only a negative / non-finite
    size falls back). Tests: `HbShaper` zero-advance accept + resolver `font-size:0`
    honored.
  - **(P2) `FontSizeResolver` over-defers invalid relative input** — FIXED. The new
    `ClassifyParentRelative` splits the numeric prefix from the EXACT unit before
    deferring: `-50%` / `-1em` / `-0.5ex` / `-2ch` (and malformed prefixes) emit
    `CSS-PROPERTY-VALUE-INVALID-001` + return `Invalid` (→ falls back to inherited),
    while `rem` is no longer mistaken for an `em` suffix (it stays deferred via
    `LengthResolver`).
  - **(P2) `FontFamilyListResolver` syntax strictness** — FIXED. The list is now
    parsed AND validated against the CSS Fonts `<family-name>` grammar: leading /
    trailing / doubled commas, unclosed quotes, junk after a quoted string, and
    digit- / punctuation-leading unquoted idents are `Invalid` + diagnostic (no longer
    silently sanitized). Quoted names + valid unquoted multi-ident names (incl.
    `-`/`_`-leading, e.g. `-apple-system`) still parse.
  - **(P2) Font-family fallback stack** — FIXED. `HarfBuzzShaperResolver.ResolveFontBytes`
    walks the whole resolved stack in author order (`MissingFont, Arial, sans-serif`
    falls through to Arial), then the configured generic default last; the cache key
    keys on the full lower-cased stack. Test: stack-walk past a missing family.
  - **(P3) Stale docs** — DONE. Refreshed the `HarfBuzzShaperResolver` class XML +
    `ShaperKey`/field docs, the `ResolverResult` UnsupportedUnvalidated backlog
    comment, the `FontFamilyListResolver` remarks, and a stale
    `HarfBuzzShaperResolverTests` comment; removed the now-dead `DefaultWeightCss` const.
- **Post-PR-#121 review — font-property hardening follow-ups** (Roland's review of the
  cycle-4 follow-ups). FIXED in the same PR:
  - **(P2) CSS-wide keywords** — `FontFamilyListResolver` now rejects an UNQUOTED
    `inherit` / `initial` / `unset` / `revert` / `revert-layer` (via the new shared
    `CssWideKeyword.Is`, which `GridLineResolver` also delegates to) so it can't be
    stored as a literal family; a QUOTED `"inherit"` stays a valid family. `font-size` /
    `font-weight` already reject CSS-wide keywords (verified by tests).
  - **(P2) CSS escapes** — `FontFamilyListResolver` decodes CSS Syntax 3 §4.3.7 escapes
    inside QUOTED strings (`"a\"b"`, `"\41 rial"`); the cache-key join in
    `HarfBuzzShaperResolver.FamilyStackKey` is now length-prefixed so a decoded name
    containing the separator can't alias.
  - **(P3) Spaced dimensions** — `FontSizeResolver.TryParseNumber` rejects any
    whitespace, so `2 em` / `50 %` are `Invalid` instead of slipping past the unit split.
  - **STILL-OPEN known gaps** (defense-in-depth + documented, not yet fully correct):
    - **CSS-wide on INHERITED font props** — `font-size`/`-family`/`-weight` are
      inherited, so the cascade's invalid-fallback yields the INHERITED value. Correct for
      `inherit` / `unset`; a gap for `initial` / `revert` (should reset to the property
      initial, e.g. `medium` / `serif`). The proper fix is a CENTRAL CSS-wide interceptor
      in the cascade (substituting initial / inherited / previous-layer before dispatch) —
      a separate cycle's scope, shared with the grid resolvers' identical limitation.
    - **Unquoted-identifier escapes** — `FontFamilyListResolver`'s unquoted path does NOT
      decode escapes (e.g. `\32 Chains`); such names are rejected (safe — falls back to
      inherited, never mis-stored), consistent with `CssTokenizer`, which also defers
      escape decoding. A unified CSS-escape pass (tokenizer + string + ident decoders) is
      the right home.
    - **`font-size: 0` text-paint guard** — DONE (cycle 5a-2-ii): zero-advance shaping is
      correct for LAYOUT, and the `TextPainter` now skips glyph emission for any run whose
      resolved `font-size` is `≤ 0`, so a zero-sized run emits nothing (no invalid PDF text
      state). (`PdfPage.ShowGlyphs` independently tolerates a `0` `Tf` as an invisible run.)
- **TODOs (remaining chain, in order)** —
  1. **CSS font-property resolution** — DONE (cycle 4): `font-size` /
     `font-family` / `font-weight` resolve (see the cycle-4 done note). A focused
     follow-up remains for the deferred font-relative cases: `rem` / viewport
     `font-size` (root-font-size threading) + general `em`/`rem` lengths on
     non-font properties.
  2. **Paint bridge** — DONE for `background-color` fills (cycle 2) +
     `border-*` edges (cycle 3). **Text runs are now UNBLOCKED** — font-size /
     family / weight resolve (cycle 4) and the shaper reads them; the PDF
     font-registration API (`PdfDocument.RegisterFont` + `PdfPage.AddFont`, the
     deferred Phase 1 Task 22) landed in **cycle 5a-1**, so the PDF side of
     embedding a subset + referencing it from a page now exists. The text PAINT
     bridge itself is **DONE — cycle 5a-2** (`5a-2-i` = the `PdfPage.ShowGlyphs`
     primitive; `5a-2-ii` = the `TextPainter`): it collects used glyph ids per
     resolved font from `BoxFragment.InlineLayout` → subsets via `TtfSubsetter` +
     `EmbeddedTtfFont.Build` → `RegisterFont` → `PdfPage.AddFont` → emits
     `BT`/`Tf`/`Td`/`Tj` at baselines. The shaper is kept ALIVE past paint
     (`PdfRenderPipeline` made it method-scoped) so the painter subsets the EXACT
     bytes layout shaped (`HarfBuzzShaperResolver.ResolveFontProgram`) — glyph ids
     match. A run whose font can't be resolved/subset is skipped with
     `PAINT-TEXT-FONT-UNRESOLVED-001` (never a throw). Tested via the facade with a
     fixed `SyntheticFont` resolver (deterministic real glyphs). The bundled
     deterministic fallback font (TODO 4 / cycle 5b) is still needed for the DEFAULT
     facade path's determinism-for-text. **Post-PR-#127 review (folded in):** the shaper
     caches the resolved program size-independently so paint subsets the EXACT bytes
     layout shaped (no drift on a stateful resolver); font-resolution failures throw a
     dedicated `FontResolutionException` caught as a pipeline backstop (valid PDF +
     `PAINT-TEXT-FONT-UNRESOLVED-001`, never a throw); partial-alpha text is composited
     via `/ca` (`ShowGlyphs` gained an `alpha` param); and the program identity is a
     content hash so fallback stacks hitting the same face share one subset.
     **Deferred text refinements (a later cycle, e.g. 5c):** GPOS-adjusted per-glyph
     positioning (the first cut uses the embedded font's `/W` advances for inter-glyph
     spacing — "simple Td + Tj", Roland's pick); RTL visual-order line reversal; and
     per-run baseline alignment on mixed-size / mixed-font lines (baseline is per-line,
     from the block line-height + the run font's ascent, mirroring `BlockLayouter`'s
     inline-only block model — there is no explicit baseline-Y in the IR).
     Constant-alpha compositing is DONE (the Phase 4 paint-alpha pass:
     partial-alpha background + border colors composite via PDF ExtGState `/ca` —
     `PdfPage.FillRectangle` gained an `alpha` param + a per-page `/ExtGState` resource;
     the `PAINT-*-ALPHA-APPROXIMATED-001` diagnostics are retired). Background images +
     border-radius shipped; **Phase 4 native gradients shipped** — `linear-gradient` (PDF
     ShadingType 2) + `radial-gradient` (ShadingType 3) backgrounds via
     `PdfDocument.RegisterAxialShading` / `RegisterRadialShading` + `PdfPage.PaintShadingInRect`.
     (PR #209 review hardened these: FunctionType 3 `/Bounds` are strictly increasing for
     terminal/leading hard stops; `to <corner>` is aspect-ratio correct; radial `at <position>`
     classifies axes + rejects duplicate/misordered pairs; identical gradients share one color
     function/shading; multi-layer lists are rejected.)
     **Phase 4 shadows + 2D transforms shipped** (PR 2): `box-shadow` (sharp → native filled
     rounded rect; blurred → the Skia `ShadowRasterizer` bridge → image XObject + `/SMask`),
     `text-shadow` (offset glyph run under the text), and 2D `transform`
     (`translate`/`scale`/`rotate`/`skew`/`matrix` → a `cm` about `transform-origin` wrapping the
     decoration + text passes). **Gradient residuals (Phase 4 follow-ups):** elliptical radial
     shaping via a CTM scale (the first cut paints ellipses circularly by their scalar extent),
     `repeating-linear/radial-gradient`, conic gradients (Skia raster), length-positioned color
     stops + color hints, per-stop alpha (a soft-mask alpha shading), multi-layer background-image
     lists, and **gradient `background-clip` / `background-origin` insets** (PR #209 Copilot —
     gradients paint/clip against the border box; the `url()` image path already honors origin/clip
     + inset radii). **Shadow / transform residuals (Phase 4 follow-ups):** `box-shadow` INSET
     (outset-only first cut; `CSS-BOXSHADOW-UNSUPPORTED-001`) + per-corner blur radii (the blur
     raster uses one representative radius); `text-shadow` INHERITANCE to descendant text (only the
     box's own declared value is read today) + true GLYPH BLUR (the blurred case paints a sharp
     offset, `CSS-TEXTSHADOW-UNSUPPORTED-001`); `transform` faithful 3D PROJECTION (genuinely-3D
     functions flatten to identity, not an orthographic projection) + the transformed-element
     STACKING CONTEXT (the PR #210 [P1] fix made transforms SUBTREE-local — a box's effective `cm`
     composes its ancestors' transforms via `TransformResolver`, so a child of a transformed element
     transforms too — but each fragment still wraps that `cm` independently, NOT as one isolated
     z-order group; a transformed ancestor split onto another page is also skipped) + `em`/`rem`/`%`
     lengths in transform offsets / `transform-origin`. The `NetPdf.Paint` `DisplayCommand` IR still has no
     fragment→command or command→PDF consumer — the bridge emits straight to `IContentStream`.
  3. **Facade** — DONE for the single-page path (cycle 2:
     `HtmlPdf.Convert` / `ConvertAsync` / `ConvertDetailed` →
     `PdfRenderPipeline`; page size/margins → `MediaBox` + content area
     from `HtmlPdfOptions`). REMAINING:
     - **Multi-page driver** (loop `AttemptLayout` over continuations, a `PdfPage`
       per fragmentainer). The pipeline-level driver is straightforward — a working
       prototype is preserved on the local `wip-multi-page-driver-blocked` branch
       (loops via `LayoutRetryCoordinator`, fresh sink per page, shaper + document
       shared, narrows `PDF-CONTENT-OVERFLOW-TRUNCATED-001`). **BUT it is BLOCKED on a
       layout-engine prerequisite discovered while wiring it: `BlockLayouter` fragments
       only the layout ROOT's DIRECT children across pages; a NESTED block container
       lays out ALL its children on the current page.** Direct-layouter probe
       (`root → wrapper → 6×200px` on a 500px page, Strict): page 0 = `PageComplete`
       with all 7 fragments (1200px, overflowing), page 1 = `AllDone` with 0 fragments.
       Since every facade document nests content under `html → body`, real content can't
       paginate until **nested-container fragmentation** lands in `BlockLayouter` (a
       substantial Phase 3 layout task — NOT a pipeline change). Until then, content past
       the first fragmentainer is clipped + surfaced via `PDF-CONTENT-OVERFLOW-TRUNCATED-001`.
       **UPDATE (multi-page driver cycles 1–2 — DONE):** nested-container fragmentation landed —
       `EmitBlockSubtreeRecursive` consults the resolver before each plain BLOCK-FLOW child + returns a
       `BlockContinuation` on block-axis overflow, so nested block content paginates at child boundaries at
       arbitrary depth. **CORRECTION (non-block-pagination audit — the cycle-8 "non-block modes don't
       paginate" finding was a FALSE NEGATIVE):** the cycle-8 probe used explicit cell/item heights, which
       collapse to ~one text-line tall (a `<td><div style="height:200px">` renders short), so its test
       containers never exceeded one page — it concluded "no pagination" when the wiring was actually present.
       An empirical re-audit (facade tests in `HtmlPdfConvertTests`, genuinely page-exceeding content) found:
       **TABLE row split, `<thead>`/`<tfoot>` REPEAT, and MULTICOL all ALREADY paginate** through the driver
       (their layouters propagate `TableContinuation` / `MulticolContinuation` through `BlockLayouter`'s
       dispatch exactly like block content); **GRID paginates with explicit `grid-template-rows`**
       (`GridContinuation` propagates via `IsPaginatableGrid`). These are now pinned at the facade level.
       **The genuine gaps were FLEX-COLUMN and GRID-with-implicit-rows.**
       **FLEX-COLUMN — DONE (non-block-pagination arc):** a tall `flex-direction: column` + nowrap container
       now splits at flex-ITEM boundaries (`FlexLayouter` main-axis item split → `FlexContinuation.ItemIndex`,
       propagated by `BlockLayouter` with a natural-size / page-budget DUAL-INPUT mirroring the grid page-budget
       so flex resolution sizes items naturally instead of shrinking them to fit the page). `IsPaginatablePerStyle`
       now accepts column-nowrap; column-reverse + column-wrap stay atomic (documented below).
       **GRID with IMPLICIT rows — DONE (non-block-pagination completion):** a grid sized by IMPLICIT tracks
       (`grid-auto-rows` / auto-placed rows, NOT `grid-template-rows`) now paginates too. The wrapper
       pre-measure `PreMeasureGridRowExtent` no longer early-returns 0 on an empty `grid-template-rows` —
       `GridSizing.Resolve` already generates implicit tracks (CSS Grid §7.4), so the natural row extent is now
       measured for explicit AND implicit rows, the wrapper overflows, and the (already-wired) grid pagination
       engages. Covered at the recursion path, with `1fr` columns, and as the root's direct child (outer
       dispatch). **REMAINING — prioritized backlog (each is its own substantial / risky standalone task;
       investigated + deferred to keep this PR's regression surface small):**
       1. **flex item CONTENT layout — DONE** (highest value; flex/grid item-content + spurious-overflow PR).
          `FlexLayouter` now lays out each flex item's inner content (text / block children) via a nested
          `BlockLayouter` into a per-item `BufferingMeasureSink`, flushed at the item's FINAL (re-anchored)
          content-box origin in the emission loop (only COMMITTED items flush; out-of-flow descendants stay
          outer-pass-owned). Column AUTO-height items CONTENT-SIZE to the measured block extent (closing the
          `<div>text</div>` collapse-to-0 gap), feeding justify-content + the column item-split budget. The
          common `<div>text</div>` item has DIRECT inline children, so a new OPT-IN `BlockLayouter`
          `layoutRootInlineContent` flag makes a nested layouter whose root is itself inline-only emit the
          root's own inline content (the block-only child loop otherwise skipped it); gated → zero blast radius
          outside flex/grid. Shared `NestedContentMeasurer` + `BufferingMeasureSink` are the DRY home.
          STILL DEFERRED: content-SIZED (auto-height) column PAGINATION (the wrapper pre-measure
          `PreMeasureFlexMainExtent` still sums declared item heights → an auto-height column doesn't drive the
          wrapper overflow that engages pagination; explicit-height columns DO paginate + render); ROW main-axis
          content-WIDTH (max-content) sizing (row auto-width items keep their flex-resolved width, content
          overflows a narrow box — grid's zero-cell contract). **`flex-grid-item-content-border-box-placement`
          (named deferral, PR-#182 review P3):** the nested content is placed at the item's BORDER-box origin —
          its own padding / border are NOT inset (content overflows into the padding strip), mirroring grid's
          cycle-1 approximation; pinned by `Flex_item_own_margin_and_padding_do_not_offset_inline_content`.
          **PR-#182 review HARDENING (landed in the same PR):** **(P1)** a new `BlockLayouter`
          `disableFlexPagination` flag (mirrors `disableGridPagination`) — nested content callers
          (`NestedContentMeasurer`, `DispatchGridItemContents`, the table cell) DISCARD the layout result, so an
          un-suppressed nested column-flex split would `PageComplete(FlexContinuation)` + silently DROP the
          deferred items; now the nested flex is atomic (content overflows). The flex subtree-extent projection
          is gated by it too. **(P2)** flex item content diagnostics are BUFFERED per item + flushed only when
          the item COMMITS (a deferred item's buffer is discarded + re-generated on its page) — no longer
          suppressed (`diagnostics: null`) nor duplicated across pages. **(Copilot)** the nested-content root's
          OWN margins are SUPPRESSED (`DispatchInlineOnlyBlock(suppressOwnMargins:true)`) so a margined item's
          text isn't shifted + its measured extent isn't inflated (the margin double-count is FIXED, not
          deferred); and `CollectInlineTextRuns` now SKIPS out-of-flow inline descendants so a `position:absolute`
          span inside an inline-only item doesn't join the line (it's anchored by the abspos pass instead).
       2. **grid CONTENT-sized rows — DONE** (same PR). `GridSizing.Resolve` gained an optional
          `GridContentMeasurer` callback; `ItemOuterContribution` measures a cell's content block extent at its
          column width when the declared height is auto/0 (was 0 → `LAYOUT-GRID-ZERO-SIZED-CELL-CONTENT-SKIPPED-001`).
          Wired at the `GridLayouter` emission Resolve AND `BlockLayouter.PreMeasureGridRowExtent` (so the wrapper
          grows + content-sized auto-row grids PAGINATE, like implicit-row grids), each memoized per item box
          (a row-spanning intrinsic item would otherwise re-measure per track). Grid items also opt into
          `layoutRootInlineContent` so inline-text cells render. STILL DEFERRED: content-WIDTH (max-content)
          COLUMN sizing (columns stay declared-width-only); the `min-height` floor isn't honored in the
          content-determined branch; fr-under-indefinite interplay unchanged. **Measurement caching (partial,
          same-instance only):** the `GridLayouter`'s content-extent + max-content-width caches are now INSTANCE
          fields that persist across that instance's `AttemptLayout` attempts (measurement-cache cycle). The key
          is the FULL set of inputs `NestedContentMeasurer.Measure` consumes — `(item, available inline width,
          block budget, writing mode, RTL)` — so a hit only reuses a value measured under identical inputs (PR
          #187 review [P1] #2: a percent-height cell measures a different extent under a different budget, so the
          budget is in the key — no stale cross-budget reuse). **Cross-COMPONENT per-conversion cache — DONE:** a
          shared `GridMeasurementCache` (NetPdf.Layout, same full key) is allocated ONCE at the root pipeline
          (`PdfRenderPipeline`) + threaded through the `LayoutContext` (as `object?`, cast at the consumers —
          captured into a `BlockLayouter._gridMeasureCache` instance field at AttemptLayout entry so the
          context-less `PreMeasureGridRowExtent` + the nested-dispatch contexts reach it). Both the pre-measure
          pre-grow AND the `GridLayouter` emission Resolve PREFER it (per-instance caches stay the null-cache
          fallback), so a content cell is shaped ONCE per conversion instead of 2× — AND successive page dispatches
          reuse prior pages' measurements (the per-page fresh `GridLayouter` no longer re-shapes). Byte-identical
          (deterministic values); a facade test asserts the pre-measure + emission of one grid total ONE measure
          pass. STILL DEFERRED: a grid nested inside a flex-item / table-cell CONTENT measure (`NestedContentMeasurer`'s
          inner context doesn't carry the cache — a deep-nesting edge); aligning `PreMeasureGridRowExtent`'s
          dry-run writing mode to the cascaded value (it stays horizontal-tb / LTR, so a non-default writing-mode
          grid misses the cross-component hit — correct, just unshared).
       3. **explicit-height flex-column spurious `PAGINATION-FORCED-OVERFLOW-001` — DONE** (same PR). The
          subtree-extent measure (`MeasureSubtreeVisualBlockExtentRecursive`) now PROJECTS a paginatable flex
          descendant to `min(authored, pageBlockSize)` — EXACTLY like the existing paginatable-grid projection —
          so an auto-height wrapper whose subtree read the flex's rigid explicit height (e.g. 2000px) no longer
          trips the ancestor's top-level forced-overflow path (the flex splits cleanly at item boundaries via
          the recursive dispatch). An auto-height flex returned 0 there, which is why only the explicit-height
          case showed the warning.
       3b. **inline-text item + background double-paint — FIXED** (same PR; discovered while shipping #1). A
          flex/grid item with BOTH a background and inline text emitted the box decoration TWICE (the item's
          geometry fragment + the inline-only content fragment, both box == the item) — benign for an opaque
          fill, a visibly darker band for a translucent one. New OPT-IN `BoxFragment.SuppressBoxDecoration`
          (set on the content fragment by `BufferingMeasureSink` / grid's `TranslatingFragmentSink` when
          `fragment.Box == the item`) makes `FragmentPainter` skip that fragment's background / borders / outline
          (text still paints in its own pass). Default false = byte-identical for every other fragment.
       4. **column-reverse flex pagination — DONE** (backlog-completion PR, first cut). A
          `flex-direction: column-reverse` now PAGINATES at item boundaries in VISUAL (reverse-DOM) order:
          `FlexLayouter.IsPaginatablePerStyle` admits column-reverse, and the emission REVERSES the
          (fresh, per-attempt) item sequence + emits it FORWARD (reusing the entire forward column item-split:
          re-anchor + cut by page budget), instead of the bottom-packed reverse FLIP. Gated to the PAGINATING
          case ONLY — non-paginating column-reverse keeps its flip, byte-identical; reversing the order is safe
          because the single column-nowrap line covers every item and the flex / content-measure results are
          indexed by DOM index. **row-nowrap intra-item content fragmentation — DONE (first cut):** a
          `flex-direction: row` / `nowrap` flex whose item CONTENT is taller than the page splits the content
          across pages at a SHARED cross cut (all items continue at the same cross position on the next page) —
          `IsPaginatablePerStyle` admits row-nowrap, a new `FlexContinuation.ConsumedCrossExtent` accumulates the
          cut, `BufferingMeasureSink.FlushRangeTo` slices each item's buffer to `[cut, cut + budget)`
          (partition-by-top, force-overflow straddlers — no loss / no double), and the dual-input pagination flag
          (renamed `outerFlexDualInputPaginating` / `nestedFlexDualInputPaginating`) extends from column to
          row-nowrap. **Auto-height row items — DONE** (a later PR): `PreMeasureFlexCrossExtent` is now
          CONTENT-AWARE (mirrors the column `PreMeasureFlexMainExtent`) — an auto-height row item contributes its
          measured content block extent, so the auto-height wrapper overflows + the split engages. **PR #189
          review (P1) — flex-RESOLVED width:** the pre-measure resolves each item's main (inline) width through the
          SAME §9.7 path FlexLayouter emits at (the extracted `FlexLayouter.ResolveFlexLineMainSizes`), so a
          `flex: 0 0 150px` / `flex-basis: 50%` item is measured at its resolved width — not the raw declared /
          container width (which under-counted wrapped height → skipped pagination → clipped). **PR #189 review
          (P2) — measurement cap:** the pre-measure + emission measure into
          `NestedContentMeasurer.EffectivelyUnboundedBlockBudgetPx` (a NAMED practical cap, ~10,400 inches — was a
          magic `1_000_000`); a measured extent that REACHES it surfaces `LAYOUT-FLEX-ITEM-CONTENT-TRUNCATED-001`
          (Warning) so the truncation isn't silent. STILL DEFERRED: a TRULY-unbounded measure that consumes nested
          continuations / streams the slice page-by-page (the cap is unreachable for real documents, so this is a
          robustness follow-up, not a correctness gap);
          intra-fragment splitting (an over-tall single line force-overflows); `box-decoration-break: clone`
          approximation (the box repeats per page); `flex-wrap: wrap-reverse` (cross-swap origin from the
          unfragmented size); **column-wrap** flex pagination (lines stack on the inline axis — not a fragment
          boundary); the spec-strict reverse-flow fragment ORDER (a reasonable visual-order interpretation shipped).
       5. **compound `@page` selectors — DONE** (`chapter:first`; same PR). `AtPageRules.MatchSelector` now
          matches a COMPOUND `<name>:<single-pseudo>` — the named part PLUS the pseudo's axis, so it outranks the
          bare named page per CSS Page 3 §3.1 (named + `:first`/`:blank` → tier 5; named + `:left`/`:right` →
          tier 4; both above the bare named tier 3). The existing single-selector tiers (0/1/2/3) are UNCHANGED
          (no churn). Reachable end-to-end via the wired per-page margin-box path (a page named `chapter` + first
          paints `@page chapter:first`'s margin boxes). **PURE-pseudo + multi-pseudo compounds — now DONE**
          (pure/multi-pseudo cycle, branch `phase-3-riders-perpage-geometry-inline-img-grid-cols`): `MatchSelector`
          models the full §3.1 (A,B,C) specificity tuple (A = named page, B = `:first`/`:blank` count, C =
          `:left`/`:right` count) encoded as `A*100 + B*10 + C`, so `:first:left` (11) + `chapter:first:left` (111)
          match; `EnumeratePageRulesWithMediaInfo(ctx)` switched from a 0..5 tier array to a stable sort. The
          single-page context-FREE view (size / first-page geometry) still classifies only bare / `:first`.
       6. **register `page` / `object-position` as first-class properties — DONE** (same PR). Both now have a
          properties.json entry + a value resolver (`PropertyType.Position` → `PositionResolver` validates a
          `<position>`; `PropertyType.PageName` → `PageNameResolver` validates `auto | <custom-ident>`), so
          `@supports (object-position: …)` / `@supports (page: …)` answer correctly + invalid values surface
          `CSS-PROPERTY-VALUE-INVALID-001`. SAFE because the cascade `GetWinner().ResolvedValue` is the
          var-substituted RAW value (independent of typed resolution): the image painter still reads
          object-position raw (the `ImgSpec` seam) and the named-page machinery still reads `page` raw onto
          `Box.PageName` — the typed slot is a `Deferred` raw-text carrier, consumed downstream. **§3.6 component
          ORDER / axis-conflict — now DONE** (axis-conflict cycle, branch
          `phase-3-riders-perpage-geometry-inline-img-grid-cols`): `PositionResolver` classifies each component
          and enforces the §3.6 grammar — `top bottom` / `left right` (same axis twice), `20px left` / `top 20px`
          (a length-percentage fixing the X-then-Y order), `left 10px right 5px` (two same-axis edges), and
          `center center center` (leftover) all report `@supports` FALSE; the painter already fell back +
          diagnosed these, so rendering is unchanged.
       7. **content-SIZED auto-height column-flex PAGINATION — DONE** (same PR). `PreMeasureFlexMainExtent` is
          now CONTENT-AWARE (mirrors grid's `PreMeasureGridRowExtent`): a content-determined (auto-height) item
          contributes its measured content block extent (memoized per item box) instead of 0, so an auto-height
          column whose items are content-sized overflows the wrapper + the (paginatable-flex) split engages.
          Explicit-height items keep their declared height (byte-identical).
       (Intra-row table-cell / grid-item splitting stays row-atomic per the existing deferrals; row-NOWRAP
       intra-item content fragmentation shipped a first cut [explicit-height items]; column-wrap + row-flex
       AUTO-height intra-item fragmentation remain the open non-block items — pure/multi-pseudo compound `@page` +
       `<position>` axis-conflict + grid content-WIDTH columns + per-page `@page` geometry all shipped in the
       riders PR `phase-3-riders-perpage-geometry-inline-img-grid-cols`.)
     - **`@page` rule** (Phase 3 Task 21). **Cycle 1 — margins — DONE:** a bare
       `@page { margin… }` overrides the page margins per side (`AtPageMarginResolver` in
       `src/NetPdf.Css/PagedMedia/` walks `Phase2Result.Sheets` → resolves the `margin-*`
       longhands AngleSharp expands → px; `PdfRenderPipeline` applies them, CSS winning by
       default). Post-PR-#130 review hardened it: applicability mirrors the cascade (skips
       disabled sheets, honors `sheet.MediaQuery` against the print `CssMediaContext`, recurses
       matching `@media`), percentage margins resolve per-axis (left/right→width, top/bottom→
       height, CSS Page 3), and `!important` wins per side (importance then source order).
       **Cycle 2 — size — DONE:** a bare `@page { size }` overrides the page size when
       `PreferCssPageSize`. The pre-pass (`CssPreprocessor.ParsePageRule`) recovers the `size`
       descriptor AngleSharp drops (`CssPageRuleRecovery.SizeText`) + the adapter re-attaches it
       as a synthetic declaration; `AtPageSizeResolver` resolves named sizes + `portrait`/
       `landscape` + `<length>{1,2}` + `auto` (sharing `AtPageRules` applicability with the
       margin resolver); `PdfRenderPipeline` applies it to the `MediaBox` + content area.
       Post-PR-#131 review hardened it: `size !important` is honored across `@page` rules (the
       pre-pass strips + records importance; the adapter stamps the synthetic declaration);
       percentage margins resolve against the RESOLVED page box (so `@page { size: A5; margin: 10% }`
       is relative to A5); invalid duplicate `size` grammar (`A4 letter`, `portrait landscape`)
       is rejected; and a `size` qualified by a paper-size `@media`/sheet media query
       (`width`/`height`/`aspect-ratio`/`orientation`/device-*) is ignored per CSS Page 3 §3.3.
       **Cycle 3 — margin boxes — DONE (literal + `attr()`):** the 16 CSS Page 3 §6.4 margin boxes
       (running headers/footers). The pre-pass already recovered them as `@page` child rules;
       `AtPageMarginBoxResolver` (`src/NetPdf.Css/PagedMedia/`) resolves the content-bearing ones
       to (name, raw `content`); `PageMarginBoxGeometry` computes each box's page-px region from
       the resolved size + margins (clamped to >= 0 so margins exceeding the page can't make a band
       negative); `PageMarginBoxPainter` resolves `content` (literal strings + `attr()` via
       `CssContentList`) + lays the text out as one line via `InlineLayouter`. Body + margin-box
       text paint through ONE shared `TextPainter` pass (margin fragments offset relative to the
       body's content origin), so a font shared by both is subset + embedded ONCE. Text is
       name-aligned within the box (`-left`/`-top`→start, `-center`/`-middle`→center,
       `-right`/`-bottom`→end; corners centered). `content` honors `!important` (cascade by
       importance then source order — within a box body AND across `@page` rules); a winning
       `none`/`normal` suppresses the box SILENTLY (no diagnostic). Unsupported content
       (`counter()`/`string()`/`element()`) emits a length-capped, control-char-sanitized
       `CSS-CONTENT-FUNCTION-UNSUPPORTED-001` + skips the box.
       **Cycle 4 — per-box style — DONE:** a margin box's declared `font-family`/`font-size`/
       `font-weight`/`font-style` + `color` flow through the shaper + `TextPainter`, and a declared
       `text-align` / `vertical-align` overrides the box's name-derived alignment. New
       `MarginBoxStyle.Build` resolves the box's longhands onto a rented `ComputedStyle`
       (`PropertyResolverDispatch` per longhand; unspecified → reader defaults, so `IsSet` means
       "declared"); `AtPageMarginBoxResolver` now carries each box's declarations on
       `ResolvedMarginBox`. Post-PR-#133 review hardened it: per-property `!important` cascade
       (importance then source order, within a box + across `@page` rules — incl. `vertical-align`);
       a WHITELIST (`font-*`/`color`/`text-align` materialized + `vertical-align` raw) so
       `padding`/`border`/`background` can't shift/paint the text; invalid values surface
       `CSS-PROPERTY-VALUE-INVALID-001` via a threaded sink; `justify-all` → start.
       **Cycle 5 — inheritance — DONE:** margin boxes inherit the supported (all inherited)
       properties along the CSS Page 3 chain root element → page context (`@page` declarations) →
       margin box. `MarginBoxStyle.Build` gained a `parentStyle` param (copies the inherited slot +
       side-table payload, then own declarations override); `AtPageMarginBoxResolver.PageContextDeclarations`
       exposes the `@page` rules' own declarations; the pipeline sources the root element box's style
       via `FindRootElementBox`. So `@page { color: gray; @top-center {…} }` tints the box and
       `html { font-family:… }` flows into headers/footers. Post-PR-#134 review hardened it:
       `text-align` is NOT inherited (alignment is read from the box's OWN declarations) so the
       page/root's UA-default `text-align: start` can't override the name-derived centering;
       CSS-wide keywords are handled at the cascade level (`initial` resets, `inherit`/`unset` keep
       inherited, `revert` approximated as inherited) instead of being treated as invalid leaf
       values; invalid-value diagnostics carry the declaration's source location; the page-context
       style is returned to the pool (`ReleaseFromBox`) instead of leaking.
       **Cycle 6 — `font` shorthand — DONE (+ post-PR-#135 review hardening):** the `font` shorthand
       is expanded into longhands for margin-box bodies (new `FontShorthandExpander` →
       `CssParserAdapter.ParseRawDeclarations`), so `@bottom-center { font: italic 9pt Georgia }` sets
       font-style/weight/size/family. AngleSharp never sees margin-box bodies, so this closes the gap
       (regular style rules already get `font` expanded by AngleSharp). A whole-value CSS-wide keyword
       maps to every longhand. The review made expansion ATOMIC — every generated longhand is
       validated through the production `PropertyResolverDispatch` resolvers, so any invalid part
       rejects the WHOLE shorthand (no partial style); a `/` requires a `<line-height>` (the value
       itself isn't surfaced); CSS comments are stripped quote-aware before tokenizing; the unitless
       zero is accepted. A system-font keyword (`caption`/…) or malformed value no longer silently
       vanishes — `ParseRawDeclarations` keeps the raw `font` declaration as a marker and
       `MarginBoxStyle.Build` surfaces a sanitized `CSS-PROPERTY-VALUE-INVALID-001`. **Deliberate
       approximations (review P4, pinned by tests):** `font-variant` / `font-stretch` / `line-height`
       are parsed but not surfaced (the margin-box style path doesn't read them); the CSS Fonts 4
       `oblique <angle>` form and an explicit `<font-stretch>` percentage are NOT modeled — such a
       shorthand is rejected atomically (its `<angle>`/percentage reaches the size slot and fails
       validation) + diagnosed, rather than silently mangled. Surfacing `font-width`/oblique-angle
       would need the shaper to consume them (out of the margin-box subset's scope).
       **REMAINING:**
       - **Conditional-group traversal:** `AtPageRules` walks only sheet media + `@media`; a bare
         `@page` nested in a matching `@supports` / `@layer` / `@container` does not yet contribute
         (the cascade honors those). Recursing needs the cascade's `@supports` evaluator lifted out
         of `CascadeResolver` into a shared helper.
       - **Margin boxes — parent-relative font (cycle 7) — DONE:** a margin box's parent-relative
         `font-size` (`em`/`%`/`larger`/`smaller`) and `font-weight` (`bolder`/`lighter`) now resolve
         against the inherited parent via the shared `DeferredFontResolver` (extracted from the
         box-builder's `ResolveDeferredFontProperties` so both consumers share it), called by
         `MarginBoxStyle.Build` after the cascade. `@page { font-size: 20px; @bottom-center {
         font-size: 1.5em } }` → 30px. STILL DEFERRED: `rem`/viewport/container-relative font-size
         (`TryResolveRelativeToParent` returns false → stays deferred → reader default; `rem` needs
         the root font-size threaded through).
       - **Margin boxes — `background-color` (cycle 8) — DONE:** a margin box's declared
         (non-inherited) `background-color` paints a band over the box's full region behind its
         content. `MarginBoxStyle.Build` materializes it from the box's own declarations (added to
         `CascadedStyleIds`, kept OUT of the inheritance copy); `PageMarginBoxPainter` resolves it
         (shared `FragmentPainter.TryResolveColor`, `currentcolor` = the box's own color) into a
         `MarginBoxBackgroundFill` at the full region rect, and the pipeline fills it (reusing
         `FragmentPainter.ToPdfRect`/`ColorChannels`/`Alpha` + `PdfPage.FillRectangle`) before the
         shared text pass. rgba composites via `/ca`. STILL DEFERRED: `border` / `padding` (need the
         content-origin inset the cycle-4 whitelist still avoids — they would shift the text) and
         background images.
       - **Margin boxes — `counter(page)`/`counter(pages)` (cycle 9; counter styles — Task 21) — DONE:**
         `content: counter(page)` / `counter(pages)` resolves to the page number / total via a new
         `CssContentList.PageCounters` context threaded through `PageMarginBoxPainter`; `PdfRenderPipeline`
         passes `(1, 1)` (single page → "1"). An optional `<counter-style>` (Task 21) formats it via a new
         shared `CounterStyleFormatter` (EXTRACTED from `BoxBuilder`'s list-marker numerals — `decimal`,
         `decimal-leading-zero`, `lower`/`upper-roman`, `lower`/`upper-alpha`+`-latin`, `lower-greek`; both
         callers now format identically). A non-predefined / unimplemented style (`hebrew`,
         `cjk-ideographic`, an undefined name) FALLS BACK to `decimal` (CSS Counter Styles §7.1.4 — the page
         number must never silently vanish; post-PR-#149 review P2) — `CounterStyleFormatter.TryFormat`
         stays null-returning so each caller chooses its own fallback (page counters → decimal, list
         markers → disc). STILL DEFERRED: the real multi-page numbers (gated on the multi-page driver
         below), rendering `hebrew`/`cjk-ideographic`/… AS those styles (they approximate as decimal),
         non-page `counter()` names + `counters()` (need the counter-reset/increment machinery), and
         `counter(page)` in body/pseudo content (no page context → unsupported).
       - **Margin boxes — `border` (border cycle) — DONE:** a margin box's declared `border` /
         per-side `border-<side>` shorthand (new `BorderShorthandExpander` for margin-box bodies →
         the 12 `border-*-width`/`-style`/`-color` longhands, added to `MarginBoxStyle.CascadedStyleIds`)
         strokes the box's full region via the shared `FragmentPainter.PaintBorders` (extracted from
         the body 4-edge loop), painted by the pipeline over the background band, before the text,
         ungated by `PrintBackgrounds`. The post-PR-#140 review added: a zero-area/non-finite guard in
         `FragmentPainter.PaintBorders` (a zero-height band from `@page { margin:0 }` paints no border);
         a sanitized `CSS-PROPERTY-VALUE-INVALID-001` for an un-expandable margin-box `border` marker
         (surfaced via `MarginBoxStyle`, mirroring the `font` shorthand, no longer silently dropped);
         CSS-comment stripping + whole-value CSS-wide-keyword (`inherit`/`initial`/…) handling in the
         expander. STILL DEFERRED: `border-radius`.
       - **Margin boxes — `padding` + the border content-inset (padding cycle) — DONE:** a margin box's
         declared `padding` (new `PaddingShorthandExpander` for the 1–4-value box shorthand → the 4
         `padding-*` longhands, added to `MarginBoxStyle.CascadedStyleIds`; per-side longhands pass
         through) AND the cycle-11-deferred border-width inset now push the box's text in — `PageMarginBoxPainter`
         insets by `ReadLengthPxOrZero(border-*-width) + ReadLengthPxOrZero(padding-*)` per side (the
         §4.3 border-width used-value gate makes the reserved space match what the painter strokes),
         shrinking the alignment extent to the content box while placing the line at the BORDER-box
         origin — `TextPainter.CollectFragment` already adds the box's border+padding (the body's
         border-box→content-box step), so applying the inset in both places would DOUBLE it (a bug
         caught + fixed mid-cycle). The border/background still cover the FULL region. A shared paren-aware
         `CssShorthandHelpers.SplitTopLevel` was extracted (border + padding share it). STILL DEFERRED:
         `calc()`/`min()`/etc. padding values (unsupported by the resolver → atomic-reject + diagnosed);
         and NON-ABSOLUTE padding (post-PR-#141 review P2 + Copilot) — a percentage (`10%`, resolves
         against the containing block inline size → needs the §5.3 margin-box sizing) or a font-/
         viewport-relative length (`1em`/`5vw`, left deferred by the resolver) is a valid value but the
         painter's `ReadLengthPxOrZero` honors only a `LengthPx` slot, so `MarginBoxStyle.Build`
         DIAGNOSES + drops any declared padding that didn't materialize to `LengthPx` (and that the
         resolver didn't already reject) rather than silently zeroing it. Absolute lengths (incl. the
         unitless `0`) apply as before.
       - **Margin boxes — `border-width`/`-style`/`-color` box shorthands (border-box cycle) — DONE:**
         the three 1–4-value border box shorthands (new `BorderBoxShorthandExpander` → the per-edge
         `border-{side}-{width,style,color}` longhands; the 1–4-value box→edge mapping extracted into the
         shared `CssShorthandHelpers.ExpandBoxEdges`, which `PaddingShorthandExpander` now also uses)
         distribute across the four edges. The 12 longhands are already in `MarginBoxStyle.CascadedStyleIds`
         (cycle 11), so they paint (cycle 11) + inset the text (cycle 12) with no painter change; an
         un-expandable one surfaces a marker diagnostic. STILL DEFERRED: `border-radius`.
       - **Margin boxes — `string-set` / `string()` (Task 22) + `position: running()` / `element()` (Task 23) — FIRST CUT DONE:**
         `MarginContentCollector` walks the document (document order) reading raw declared values:
         `string-set: name <content-list>` sets a named string (later assignment wins — the end-of-page
         value); `position: running(name)` registers the element's text. The result threads to
         `PageMarginBoxPainter` as a `CssContentList.MarginContentContext`, where `content: string(name)`
         pulls the named string and `content: element(name)` pulls the running element's text (an undefined
         name → the empty string). `BoxBuilder` SKIPS a `position: running()` element from the body box
         tree (detected from the raw `position` value before the keyword resolver, so no spurious
         invalid-value diagnostic). STATUS: (a) **cross-page "running" persistence** DONE (multi-page driver
         cycles 5 [named strings] + 5b [`element()`]) — `MarginContentCollector.CollectPerPage` buckets each
         `string-set` assignment AND each `position: running()` occurrence by the page its (setting / removed-
         from-flow) element laid out on — nearest rendered ANCESTOR for an inline setter or a running element,
         with a page-0 fallback for an all-running body that produced no in-flow fragment — then carries each
         value forward until re-set, so per page `string(name)` / `element(name)` (first / last) read the value
         CURRENT on that page. A running occurrence carries its WHOLE payload (text + own style + per-line
         segments + container bands) as one `RunningOccurrence`, so the per-page first/last selection stays in
         PR-#151 lockstep (the text can't pair with another occurrence's style). A SINGLE-page document short-
         circuits to the whole-document context (byte-identical to the pre-cross-page path). A running
         occurrence's PAGE is derived from DOCUMENT ORDER (post-PR-#178 review P1): a removed-from-flow running
         element buckets to the page of the next rendered element FOLLOWING it within its containing block (the
         content it heads), falling back to its nearest rendered ancestor's page (a trailing heading) then page
         0 — so direct sibling running headings under `<body>` AND multiple headings inside one multi-page
         container each resolve to their own page (the cycle-5b nearest-ancestor walk collapsed them to the
         container's first page). APPROXIMATION: the page comes from the first FOLLOWING in-block rendered
         element, not the running element's own laid-out position (it has none) — exact for the header-precedes-
         content GCPM shape, an approximation when a running element sits among intra-page splits. Still
         deferred: `string(name, start)` / `first-except`; (b) **`string-set: … content()`
         — DONE (Task 22 follow-up):** AngleSharp.Css DROPS the `content()` function from `string-set` (an
         unknown function in the unknown `string-set` property); `CssPreprocessor`'s recovery (gated to
         `string-set` + a `content()` value) re-injects the dropped declaration into the cascade, where
         `MarginContentCollector` reads it and `CssContentList.TryParseStringSet` resolves `content()` to the
         element's own text (bare `content()` / `content(text)`; the typographic targets
         `content(before|after|first-letter|marker)` stay deferred). The cascade's own selector matching
         associates the rule with the elements — no separate raw-CSS pre-pass needed; (c) **`element(name [,
         first | last])`** renders the running element's TEXT, read BOUNDED to 64 KiB
         (`MarginContentCollector.ReadBoundedDescendantText` — a DoS guard, post-PR-#150 review P2) then
         GCPM-normalized as `white-space: normal` (Task 23 follow-up — like `content()`), with the margin
         box's own style. The position keyword is DONE
         (Task 23): `element(name, first)` AND the no-keyword DEFAULT → the first occurrence (GCPM §7.4),
         `element(name, last)` → the exit value (`MarginContentContext.RunningElementsFirst`; the shared
         `TryReadPositionedFunction` parses both `string()` + `element()`); `start` / `first-except` bail.
         A STANDALONE `element(name)` now renders the running element's box AS the margin box's content box
         (Task 23 FULL-block first cut): its text in the element's OWN font + color, plus its OWN (non-inherited)
         `background-color` + `border-*` decoration. `MarginContentCollector.CaptureOwnStyle` captures the
         element's winning font/color longhands (inherited — WALKED from ancestors, post-PR-#151 review P2) AND
         its NON-inherited `background-color` + 12 `border-*` longhands (`DecorationOwnProperties` — the
         element's OWN winner only, no ancestor walk), first + last occurrence in LOCKSTEP with the text (review
         P1). `PageMarginBoxPainter` detects standalone `element()` (`CssContentList.TryGetStandaloneElement`) +
         builds a CONTENT `ComputedStyle` (font/color, for shaping + the `BoxFragment.TextMetricsStyle` line
         metrics, review P1) AND a box `style` with the element's decoration cascaded UNDER the box's own
         declarations (`BuildFromOwnStyle(decorationOnly: true, appendDeclarations: box decls)` — a box-declared
         `background`/`border`/`padding` OVERRIDES the element's), reusing all the box bg/border/inset machinery.
         currentcolor is ORIGIN-aware (CSS Color 4 §6.2, post-PR-#152 review P1): a box-declared decoration's
         currentcolor resolves against the box's colour, an element-declared one against the running element's.
         The element's OWN `padding-*` (self-only) insets its text + grows the shrink-to-fit box, and its OWN
         (inherited) `text-align` aligns its line (`ElementHorizontalAlignFactor`, the box's own text-align
         winning — and post-PR-#153 Copilot review, a box that DECLARES `text-align` as a CSS-wide/unknown
         keyword keeps its NAME-DERIVED default rather than deferring to the element). Post-PR-#153 review P2:
         the inherited-property walk (`NearestDeclaredWinner`) now RESOLVES CSS-wide keywords (CSS Cascade L5
         §7) — `inherit`/`unset`/`revert` continue to the ANCESTOR value, `initial` → the property initial
         (`start` for `text-align`) — so `.section { text-align: right } .rh { text-align: inherit }` aligns the
         running line right. `element()` nested BLOCK children FIRST CUT (branch
         `phase-3-task-23-element-nested-blocks-vedge-overflow`): a running element with BLOCK-level children
         (per computed `display`, UA tag default via `HtmlDefaultDisplay`) renders each block child's text on
         its OWN STACKED line (`MarginContentCollector.ReadRunningElementContent` joins the per-block
         GCPM-normalized lines with `U+000A`; `PageMarginBoxPainter` lays them out as `white-space: pre` so the
         existing multi-line stacking honors the mandatory breaks — a plain header has no `U+000A`, single-line
         path byte-identical), and a margin box whose content block-height is TALLER than its band surfaces
         `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001` AND is CLIPPED (overflow-clipping cycle): the overflow is
         truncated at LINE granularity — `PageMarginBoxPainter.MaxLinesThatFit` caps the painted lines to the
         content-box height (reading-order: the first N whole lines paint; 0 fit → decoration-only box), the
         truncated block then vertical-aligned in the content box; the diagnostic names the kept/total lines.
         **Clip-path cycle (DONE):** HORIZONTAL overflow of the surviving lines (an unbreakable run wider
         than the box / a clamped rigid sibling / a nowrap line) is CLIPPED at GLYPH level — the fragment
         carries a PADDING-box clip rect (CSS Overflow 3 §3; `BoxFragment.ClipRect`) and the shared
         `TextPainter` wraps its glyph runs in a PDF `q <rect> re W n … Q` clip path
         (`PdfPage.BeginRectangleClip`/`RestoreGraphicsState`), surfaced via the same diagnostic code
         (width-phrased message); an EXPLICIT `overflow: visible` declared on the box OPTS OUT of all of
         it — no truncation, no clip, no diagnostic (`MarginBoxStyle.OverflowVisible`, raw-read like the
         alignment readers; a fitting box carries no clip rect, so its stream is byte-identical).
         APPROXIMATIONS (documented): a VERTICALLY part-fitting line is still dropped whole (line
         granularity — the clip rect guarantees nothing paints outside the padding box, but a sliver of a
         partial line isn't painted), and clip-by-default INVERTS the spec initial (CSS Paged Media §6.2
         applies `overflow` to margin boxes with initial `visible` — page furniture spilling over the body
         is near-always unwanted, so `visible` must be DECLARED to opt out). The running element's nested
         BLOCK LAYOUT is now rendered (PR-3 task 10 — the segment-style + container-bands cycles): each
         direct block child lays out on its OWN stacked line(s) with the block's OWN inherited style, its
         text WRAPS within the box (post-PR-#154), per-segment margins / padding / line-heights apply, and a
         decorated intermediate block paints its OWN background / border as a CONTAINER BAND over its
         descendant lines. STILL deferred for `element()`: INLINE-LEVEL styling WITHIN a leaf block (a
         `<b>`/`<span>` inside a block child is flattened to that block's own style — the capture records
         per-block text + style, not per-inline-run); the box/element being SEPARATELY-decorated
         boxes (they COINCIDE — a box property overrides rather than nesting); only RELATIVE UNITS (`%`/`em`/
         `calc()`) in the element's style resolve against the page context (an approximation — exact for
         absolute font-size/color, and CSS-wide `inherit`/`initial` now resolved); the element's own `%`/`em`/`calc()`
         padding RESOLVES like the box's (relative-padding cycle; its `em` against the BOX font — an
         approximation); a MIXED list (`"x" element(rh)`) keeps the box style (GCPM: element() is standalone); (d) the `string(name, first |
         last)` position keyword is DONE (Task 21):
         `MarginContentCollector` keeps both the FIRST and LAST assignment per name (`MarginContentContext`
         gained `NamedStringsFirst`); `string(name, first)` AND the no-keyword DEFAULT → the first
         assignment (per CSS GCPM §7.3 — `first` is the default, NOT the exit value; post-PR-#149 review
         P1); `string(name, last)` → the exit value. The cross-page `start` / `first-except` keywords stay deferred (need
         the multi-page driver). Other deferrals: `border-radius` + background images. The per-box /
         page-context `ComputedStyle.Rent()` is box-owned (not returned to the pool) — a negligible miss.
       - **Margin boxes — §5.3 three-box-per-edge sizing (shrink-to-fit + explicit size + overlap distribution) — DONE:** a
         content-bearing edge box is sized along the §5.3 variable axis
         (`PageMarginBoxGeometry.MarginBoxAxis` — top/bottom → width, left/right → height; corners
         neither) either from an explicit `width` (top/bottom) / `height` (left/right) — explicit-size
         cycle — or, when that's `auto`, by SHRINKING a content-bearing box to its border-box content
         size (cycle 14 first cut), so its background/border cover the box (not the whole band). The box
         is positioned in the band by its §5.3.2.4 NAME-DERIVED role (`region.HAlign/VAlign` —
         start/center/end); the declared `text-align`/`vertical-align` aligns only the line within the
         content box (observable now that an explicit size can make the content box wider than the line;
         post-PR-#143 review). An explicit size is content- or border-box per the box's `box-sizing`
         (overflow-clip/box-sizing cycle — see below); an absolute length or a percentage of the band resolves, `auto` shrink-to-fits, and a
         DEFERRED font-/viewport-relative or `calc()` size is diagnosed (`CSS-PROPERTY-VALUE-INVALID-001`)
         + dropped so the box explicitly shrink-to-fits (post-PR-#144 review). Clamped to the band.
         Auto-sized empty (`content:""`) / failed-font
         boxes keep the full band (preserving the cycle-8 decorative band; an explicit size sizes them).
         **Overlap DISTRIBUTION + min/max-content FLEX + overflow WRAPPING:** boxes sharing an edge whose
         desired sizes would OVERLAP are resolved by `PageMarginBoxGeometry.ResolveEdgeOverlap` (wired via
         `PageMarginBoxPainter.ResolveEdgeOverlaps`): when content can wrap (some box's min-content &lt;
         its max-content AND the mins fit the band) it does a min/max-content FLEX — each box gets
         `min + (max − min) × factor`, `factor = clamp((band − Σmin)/(Σmax − Σmin), 0, 1)`, tiled
         edge-to-edge; otherwise (rigid content, or mins don't fit) the center-priority CLAMP (center
         centered, sides clamp to the gaps; no center → proportional shrink). Min-content is measured per
         box (`TryMeasureMinContentWidthPx` — re-lay-out at a tiny width, widest line = longest unbreakable
         run). A NO-OP when they don't overlap, and RIGID content (min == max) always takes the clamp path,
         so single boxes + the common short-content case stay byte-identical to the cycle-14/15 model. A
         HORIZONTAL box the distribution shrank below its single-line width then RE-WRAPS its content
         (honouring the box's computed `white-space`) to the assigned width (multi-line) so it FITS, and each
         wrapped line is ALIGNED PER LINE by the box's alignment (Task 21 — `BoxFragment.LineAlignFactor`,
         applied by `TextPainter`; default 0 keeps body fragments byte-identical, single-line margin content
         too). **Vertical-edge (height) overflow CLIPPING + `box-sizing` — DONE (overflow-clip/box-sizing
         cycle):** content taller than the content-box height is truncated at line granularity (see the
         Task-23 entry above — `MaxLinesThatFit`, kept/total surfaced via
         `PAINT-MARGIN-BOX-CONTENT-OVERFLOW-001`), and an explicit `width`/`height` honours the box's
         `box-sizing` (CSS Basic UI 4 §10: `content-box` initial — insets add; `border-box` — the size IS
         the border box, floored at the insets, the content box at 0; cascaded via
         `MarginBoxStyle.CascadedStyleIds`, non-inherited, read by `IsBorderBoxSizing`; a no-op for
         shrink-to-fit `auto`). **Vertical-edge WRAPPING + clip path + relative-unit sizes — DONE
         (vertical-wrap / clip-path / relative-units cycles):** a VERTICAL (left/right) or CORNER box's
         inline axis is FIXED (the band/corner width), so its content now WRAPS AT THAT WIDTH (a block
         container wraps at its content width — was one NoWrap line spilling horizontally), stacking lines
         down the variable axis (height shrink-to-fit, clamp + line-granularity clip apply); HORIZONTAL
         (top/bottom) boxes keep the unconstrained max-content measure that drives shrink-to-fit + the
         §5.3 flex. Horizontal GLYPH overflow clips via the padding-box `W n` clip path with an
         `overflow: visible` opt-out (see the Task-23 entry). A font-/viewport-relative explicit
         `width`/`height` (`10em`/`4ex`/`4ch`/`1.5rem`/`50vw`/`25vh`/`vmin`/`vmax`) now RESOLVES — kept as
         a deferred raw by `MarginBoxStyle`, resolved by `PageMarginBoxPainter.TryReadExplicitSizePx` via
         the shared `RelativeLengthResolver` (`em` against the BOX's resolved font-size, `rem` the root's,
         viewport units the PAGE box per CSS Paged Media; `ex`/`ch` ≈ 0.5em per CSS Values 4 §6.1.2).
         **calc() sizes + `white-space` + relative/percent PADDING — DONE (calc / white-space /
         relative-padding cycles):** an explicit `width`/`height`/`padding-*` in `calc()` now EVALUATES —
         a margin-box-scoped pre-resolver admit keeps the raw as a deferred value (`MarginBoxStyle` —
         LengthResolver has no calc machinery and would reject it; BODY calc() keeps its invalid-value
         diagnostic, a separate pickup) and the painter evaluates it via the new shared
         `CalcLengthEvaluator` (CSS Values 4 §10: sum/product grammar, whitespace-required `+`/`-`,
         type-checked `*`/`/`, nested parens/calc(), % terms against the band, relative terms via
         `RelativeLengthResolver`, used-value range clamp per §10.5; a malformed/unsupported calc — e.g.
         length×length, min()/max(), container units — is KEPT by the syntactic gate then SURFACED at
         paint time and falls back). Margin-box `white-space` IS cascaded now (an INHERITED
         `MarginBoxStyle` longhand — root → page context → box): a declared `nowrap`/`pre` keeps a rigid
         single line (no vertical-edge wrap, no min-content flex — the clamp + clip path), making the
         PR-#147 computed-white-space paths reachable by declaration. PADDING percentages resolve against
         the box's containing-block width (the edge region width — CSS B&B §8.4: all four sides use the
         INLINE-axis base) and font-/viewport-relative + calc() paddings resolve like sizes —
         `PageMarginBoxPainter.ResolveUsedPaddingInPlace` rewrites the USED px into the slot so every
         downstream reader (insets, `TextPainter`'s content origin, `FragmentPainter`) sees the same
         value; the element()'s own captured padding takes the same path (its `em` resolves against the
         BOX font — documented approximation). A HEIGHT flex for overlapping vertical siblings is
         RESOLVED BY DESIGN: their heights are rigid at the fixed band width (re-wrapping can't change a
         height without changing the width), so the center-priority clamp + line clip IS the §5.3
         resolution. **min()/max()/clamp() + rem/viewport font-size + per-edge border currentcolor — DONE
         (min/max-clamp / font-size / per-edge-currentcolor cycles):** the §10.2 comparison functions
         evaluate (in calc() AND standalone — `width: min(50%, 150px)`; same-type args,
         `clamp(MIN, VAL, MAX) = max(MIN, min(VAL, MAX))`, depth-capped like parens;
         `CalcLengthEvaluator.IsMathFunction` is the keep gate). A root-/viewport-relative margin-box
         `font-size` (`2rem`/`5vw`) resolves at paint time against the root font-size / page box
         (`PageMarginBoxPainter.ResolveDeferredFontSizeInPlace`, run on the page context BEFORE boxes
         inherit + before the em size/padding bases are read — closes the old 16px-fallback gap;
         container units still fall back). Border `currentcolor` is per-EDGE (CSS Color 4 §6.2): each
         edge falls back to its OWNER's colour — the box's when the box declares that edge's color or
         style longhand, else the running element's (`FragmentPainter.BorderEdgeCurrentColors`; the
         uniform body path delegates, byte-identical — replaces the whole-border ownership rule whose
         mixed-origin case painted every edge with the box colour). **§10.6/10.7 math functions + BODY calc first cut + element() deep recursion — DONE
         (math-fns / body-calc / deep-recursion cycles):** `round(<strategy>?, A, B?)` (nearest
         ties-up default / up / down / to-zero; B defaults to the number 1, so a length A without B is
         a type error per spec), `mod(A, B)` (sign of B), `rem(A, B)` (sign of A), `abs(A)`, and
         `sign(A)` (a NUMBER — composes in products, invalid as a whole length) evaluate in
         `CalcLengthEvaluator` (same-type args, zero step/divisor invalid, depth-capped). BODY
         properties evaluate ABSOLUTE-term math functions at cascade time: `LengthResolver` routes a
         math-function value through the evaluator with a NaN context (any %/font-/viewport term
         poisons → rejected → the diagnosed-invalid path, message naming the context-dependent
         deferral), with the §10.5 range clamp following the property's actual range (a body
         `margin-left: calc(0px - 10px)` resolves to −10px); `CssPreprocessor` RECOVERS dropped
         math-function declarations (AngleSharp drops/normalizes them — new `ContainsMathFunction`
         gate, tokenized so quoted strings don't match), and the padding-shorthand expander now
         accepts an absolute calc part. The running element's nested blocks RECURSE
         (`MarginContentCollector.ReadRunningElementContent` — a block child with block children
         contributes one stacked line per NESTED block, `MaxRunningBlockDepth` = 16 deep, the single
         64 KiB budget threading through every level; deeper nests flatten — the pre-cycle behavior).
         STILL DEFERRED: container-relative units (no container context — diagnosed + dropped). The post-PR-#159 review fixed `round()`'s negative-step inversion (the step
         normalizes to |B|), a `sign(NaN)` crash under the body NaN context (NaN now propagates to
         the surfaced path), the failure diagnostic mis-blaming context-dependent terms for
         malformed expressions (a finite-probe re-evaluation picks the right message), and
         `ContainsMathFunction` missing math functions nested inside unknown functions
         (`var(--x, calc(…))` now recovers).
       - **§10.8 trig + §10.9 exponential functions + body CONTEXT-DEPENDENT lengths (no-% slice) +
         element() per-line OWN STYLE — DONE (trig/exp / body-relative / element-segments cycles):**
         **(trig/exp)** `sin()`/`cos()`/`tan()` (number = radians, or a `deg`/`grad`/`rad`/`turn`
         angle), `asin()`/`acos()`/`atan()`/`atan2()` (→ ANGLE, consumable only by trig args — the
         top-level length gate rejects a bare one), `pow()`/`sqrt()`/`hypot()`/`log()`/`exp()` and
         the `e`/`pi` constants evaluate in `CalcLengthEvaluator` (the Term type system gained an
         ANGLE kind; `hypot()` keeps its arguments' type, so `width: hypot(30px, 40px)` is a valid
         whole value); `infinity`/`NaN` keywords + the spec's exact-asymptote values stay
         unsupported (the finite gate rejects). **(body relative lengths)** the cascade DEFERS a
         body math function whose only context dependence is font-/viewport-relative (a NaN-percent
         probe classifies it), and the new post-build `DeferredLengthResolver` pass
         (`PdfRenderPipeline`, after the `@page size` override fixes the page box) resolves ALL
         deferred font-/viewport-relative body lengths IN PLACE — `2em` / `50vw` / `calc(2em +
         10px)` on width/height/margins/paddings/offsets/line-height — em against the OWNING box's
         font-size, rem against the root element's, viewport against the PAGE box; margins/offsets
         admit negatives (`RelativeLengthResolver.TryResolve(allowNegative:)`), non-negative
         properties reject a negative unit value and §10.5-clamp a negative calc. STILL DEFERRED
         there: PERCENTAGE terms + plain percentage lengths (the containing-block base is
         layout-time), border widths (`LineWidth` never defers), `lh`/`cap`/`ic`, and the
         inherited-line-height nuance (an inherited `1.5em` line-height re-resolves against each
         inheritor's font-size — computed-value-time inheritance is approximated). **(element()
         segments)** a standalone `element()`'s stacked lines render in each LEAF block's OWN
         (ancestor-walked) font + colour — `MarginContentCollector` records one `RunningSegment`
         per line (lockstep with the text/own-style capture), and `PageMarginBoxPainter` shapes
         each as its own `TextRun` (per-run shaping/painting already existed), the line pitch +
         `TextMetricsStyle` following the LARGEST segment font (uniform-pitch approximation).
         STILL DEFERRED there: per-line decoration/margins (a leaf block's own
         background/border/margin band), per-line `text-align` (captured, not consumed — one
         line-align factor per box), true per-line pitch, and the box/element separately-decorated
         nesting.
       - **`border-radius: Rx / Ry` elliptical slash (body + margin-box) — DONE (border-radius-elliptical
         cycle):** the explicit two-radii-per-corner `border-radius: <h-list> / <v-list>` form now renders
         ELLIPTICALLY (it previously dropped to square). AngleSharp drops the slash, so the body
         `CssPreprocessor.ScanDeclarations` recovers it (gated to a well-formed TOP-LEVEL slash via
         `BorderRadiusShorthandExpander.HasTopLevelSlash` — a circular form AngleSharp already expands; a `/`
         inside `calc()` is a division) and the (now slash-aware) `BorderRadiusShorthandExpander.TryExpand`
         expands BOTH sides by the 1–4-value box distribution: the horizontal radii onto the
         `border-{corner}-radius` longhands + the vertical radii onto FOUR new INTERNAL
         `-netpdf-border-{corner}-radius-y` properties (vendor-prefixed in `properties.json`, so real
         `@supports` queries for the standard radius properties are unaffected). `FragmentPainter.ReadCornerRadii`
         reads each corner's vertical radius from the `-y` slot (falling back to the horizontal slot resolved
         against the box HEIGHT when unset — the circular / `%`-ellipse pre-cycle behavior). The margin box
         shares it (the expander runs in `CssParserAdapter.ParseRawDeclarations`; the four `-y` ids JOINED
         `MarginBoxStyle.CascadedStyleIds`; a malformed slash that fails to expand is diagnosed
         `CSS-PROPERTY-VALUE-INVALID-001` as before). FONT-/VIEWPORT-relative margin-box radii (`em`/`ex`/`ch`/
         `rem`/`vw`/`vh`/`vmin`/`vmax`) now RESOLVE (margin-box-relative-radius cycle —
         `PageMarginBoxPainter.ResolveDeferredBorderRadiusInPlace` rewrites the deferred corner longhands to
         used px via `RelativeLengthResolver` after the box font-size resolves, before `ReadCornerRadii`); a
         `calc()` / container-unit margin-box radius STILL defers → square (its `%` base is the box dim, not
         final at resolve time — a documented gap, like the body's calc radii). STILL DEFERRED: rounded
         NON-uniform borders (the OUTER corners now round via a clip — rounded-nonuniform-borders cycle;
         per-corner arc segments stay deferred).
       - **margin-box `border-radius` PARITY (per-corner + `%` band, rounded uniform border, rounded
         image clip) — DONE (margin-box-border-radius cycle, 3 tasks):** the margin box's border-radius is
         brought to parity with the body. Margin-box bodies BYPASS AngleSharp, so the `border-radius`
         shorthand is expanded by the NEW `BorderRadiusShorthandExpander` (1–4 values → the four corner
         longhands, reusing `CssShorthandHelpers.ExpandBoxEdges`; a TOP-LEVEL `Rx / Ry` slash now also
         expands the vertical radii onto the internal `-y` longhands — border-radius-elliptical cycle — and a
         `/` inside `calc()` is a division that evaluates) inside
         `CssParserAdapter.ParseRawDeclarations`, and the four corner longhands JOINED
         `MarginBoxStyle.CascadedStyleIds` so they cascade onto the box's `ComputedStyle` (importance +
         CSS-wide + validation for free). **(Task 1)** the band fill reads PER-CORNER radii
         (`FragmentPainter.ReadCornerRadii`, now internal) against the FINAL box dims at PASS 2 — absolute
         or `%`-ellipse — via the per-corner `PdfPage.FillRoundedRectangle(CornerRadii)` (the uniform-circular
         case keeps the byte-stable single-radius path); the old raw `ReadBorderRadiusPx` first cut is
         removed. **(Task 2)** the rounded uniform border comes FREE — `MarginBoxBorder` carries the box's
         style through `FragmentPainter.PaintBorders`, which already reads the corner longhands + paints the
         filled ring. **(Task 3)** the background-image clip rounds (`MarginBoxBackgroundImage.ClipRadiiPx`
         = the box radii inset to the clip box via `FragmentPainter.InsetRadii`, threaded to the shared
         tiler). Post-PR-#174 review: an INVALID/malformed margin-box `border-radius` (`8px bogus`, an
         unbalanced function, or a NEGATIVE radius — `border-*-radius` joined `NonNegativeProperties`) is
         DIAGNOSED (`CSS-PROPERTY-VALUE-INVALID-001`) via `MarginBoxStyle` instead of silently dropped;
         a `/` inside `calc()` is a division that evaluates (paren-aware, self-review P3). Font-/viewport-
         relative margin-box radii (`em`/`vw`/`rem`/…) now RESOLVE (margin-box-relative-radius cycle); a
         `calc()` / container-unit margin-box radius still renders square (documented). Rounded NON-uniform
         borders FIRST CUT (the outer corners round
         via a clip — rounded-nonuniform-borders cycle; per-corner arc segments / inner corners stay
         deferred). (The elliptical `Rx / Ry` slash SHIPPED later — border-radius-elliptical cycle, see that
         entry above.)
       - **`outline` — DONE (outline cycle, CSS UI 4 §5, 3 tasks):** `outline-width` / `-style` /
         `-color` + the `outline` shorthand (AngleSharp expands it into the three longhands) + `outline-offset`
         are registered in `properties.json` (so `@supports` reports them); `outline-offset` is recovered from
         an AngleSharp-beta drop via `CssPreprocessor.KnownDroppedProperties` (a verbatim longhand recovery,
         like `white-space`). **(Task 1 — paint)** the outline paints as a filled RING just OUTSIDE the border
         box — it does NOT affect layout — via the shared `PdfPage.FillRoundedRectangleRing` (the annulus
         between the border box grown by `outline-offset` [inner] and again by `outline-width` [outer]), in
         `outline-color` (initial `auto` → currentcolor); `outline-style: none` or a non-positive width paints
         nothing. **(Task 2 — `outline-offset`)** a positive offset pushes the outline outward, a negative one
         inward. **(Task 3 — rounded outline)** a `border-radius` rounds the outline to follow the box (each box
         corner radius grown by the gap to that outline edge — offset + width for the outer, offset for the
         inner; a SHARP box corner stays sharp, §5.3), reusing `CornerRadii`. **Post-PR-#173 review (4 numbered +
         2 Copilot, replied + resolved):** **(P2)** `outline-style: hidden` is INVALID (CSS UI 4 §5.2 excludes
         hidden — `@supports (outline-style: hidden)` is now FALSE; outline-style uses its OWN keyword indices
         since the table differs from border-style); **(P2)** `outline-color: auto` is admitted (CSS UI 4 retired
         `invert` and makes `auto` the initial) → currentcolor, via a dispatch special-case; **(P2)** an extreme
         negative `outline-offset` clamps PER AXIS to ≥ −½ the box dimension BEFORE the origin + size, so the
         collapsed outline stays CENTERED instead of drifting; **(P3 + Copilot)** `GrowRadii` clamps a component
         a large negative offset would drive below 0 (matching `ReduceRadii`); **(Copilot)** `outline-width` is a
         non-negative `<line-width>` (`NonNegativeProperties` — a negative value invalidates + falls back to
         `medium`); **(Copilot)** borders + outline SHARE one style-approximation flag so
         `PAINT-BORDER-STYLE-APPROXIMATED-001` fires once per conversion. STILL DEFERRED: non-solid
         `outline-style` (dotted/dashed/double/groove/ridge/inset/outset painted SOLID + diagnosed; `auto` paints
         solid without a diagnostic); `outline-color: auto`'s true UA colour (approximated currentcolor).
       - **body `border-radius` COMPLETION (per-corner + `%` band fill, rounded uniform border
         strokes, rounded background-image clip) — DONE (border-radius-completion cycle, 3 tasks):**
         the body border-radius first cut (uniform-circular band fill only) is finished. **(Task 1 —
         per-corner + `%` band)** the band rounds with PER-CORNER radii (the `border-radius` 1–4-value
         shorthand expands to the four corner longhands; `FragmentPainter.ReadCornerRadii` reads each as
         an absolute length [circular] or a PERCENTAGE, which resolves against the box WIDTH for the
         horizontal radius and the box HEIGHT for the vertical — so a non-square `50%` box is an ELLIPSE,
         CSS B&B 3 §4.1), filled by the new per-corner elliptical `PdfPage.FillRoundedRectangle(…,
         CornerRadii, …)` (§4.2 overlap-scaling via `CornerRadii.NormalizedFor`); the UNIFORM-circular
         case keeps the byte-stable single-radius path. **(Task 2 — rounded uniform border)** a box with
         a border-radius AND a uniform border (same paintable style/width/colour on all four edges,
         `FragmentPainter.TryUniformBorder` — widths compared with a tolerance so equivalent mixed-unit
         lengths don't fall back) paints ONE filled RING (`PdfPage.FillRoundedRectangleRing` — the
         even-odd annulus between the border box [outer, the border-box radii] and the padding box
         [inner, radii reduced by the full border width]) instead of the four square edge rects. A FILLED
         ring, NOT a centerline stroke (post-PR-#172 review P1+P2): its outer corner is EXACT for any
         border width — a small radius under a thick border keeps its rounding (the inner corner goes
         sharp, exactly CSS) — and it composites the border colour's alpha correctly (a fill → `/ca`, not
         a stroke's `/CA`). **(Task 3 — rounded background-image clip)** a border-radius rounds the
         background-image clip (`PdfPage.BeginRoundedRectangleClip`, the background-clip box's radii inset
         per side) on BOTH the per-tile loop and the tiling-pattern paths; zero radii fall back to the
         rectangular clip (byte-identical). The explicit two-radii `Rx / Ry` slash spelling SHIPPED later
         (border-radius-elliptical cycle — recovered into 4 internal `-netpdf-…-radius-y` longhands; see that
         entry above). **Rounded NON-uniform borders — FIRST CUT DONE (rounded-nonuniform-borders cycle):**
         a border-radius with per-side-differing border widths / styles / colours can't use the uniform
         ring, so `FragmentPainter.PaintBorders` now CLIPS the four square edge rects to the rounded
         border-box outline (`PdfPage.BeginRoundedRectangleClip` … `RestoreGraphicsState`) — the box's OUTER
         corners follow the radius (matching the already-rounded background band + image clip) instead of
         poking out square. Applies to a body block AND a margin box. STILL DEFERRED (approximations): the
         per-corner ARC SEGMENTS that transition between two edges' widths/colours (the INNER corners stay
         square + a corner where two differently-coloured edges meet shows a hard split, not a diagonal
         miter). (The MARGIN-box border-radius reached parity in the margin-box-border-radius cycle — see
         the entry above.)
       - **body `border-radius` (background band) + `background-attachment` + margin-box
         `background-origin`/`-clip` — DONE (body-radius / bg-attachment / margin-box-origin-clip
         cycles):** a UNIFORM absolute `border-radius` rounds a BODY block's background COLOR band
         (`FragmentPainter.PaintBackground` → `PdfPage.FillRoundedRectangle`, clamped to half the
         shorter side; the four corner-radius longhands now REGISTERED — `properties.json`
         LengthPercentage, expanded from the `border-radius` shorthand, so `@supports` reports
         them). `background-attachment` is REGISTERED for VALIDATION only (keyword
         `scroll`[initial]/`fixed`/`local`) so `@supports` reports it + an invalid value is
         diagnosed — but rendering does NOT consume the value yet (PARSE-ONLY metadata): for PAGED
         media there is no scroll, so every value paints element-relative, `fixed` page-relative
         positioning being silently approximated (NOT diagnosed — the value is valid; post-PR-#171
         review P2 made the code comment + docs honest). A page-MARGIN box's `background-image` now
         honors `background-origin` (positioning area) + `background-clip` (paint rect) like a body
         block — the body `FragmentPainter.BackgroundAreaInset` (made internal) is reused on the
         margin box's `ComputedStyle`, the origin area + clip rect riding `MarginBoxBackgroundImage`
         into the shared tiler (the origin/clip keywords flow through the box's cascade — importance
         + CSS-wide + invalid-value diagnostics — post-PR-#171 review P2, not RawDeclarationWinner;
         the inset sums are clamped to ≥ 0 so a thin box with large border/padding can't produce a
         negative paint rect — review P1). STILL DEFERRED (much of this body-radius list SHIPPED in the
         border-radius-completion cycle — see the entry above; the `Rx / Ry` elliptical slash SHIPPED in the
         border-radius-elliptical cycle): rounded NON-uniform border strokes — FIRST CUT in the
         rounded-nonuniform-borders cycle (the outer corners round via a clip; per-corner arc segments
         transitioning between edges stay deferred). The margin-box per-corner radius + rounded border +
         image clip reached parity in the margin-box-border-radius cycle.
         `background-attachment: fixed` PAGE-relative positioning; gradients (Phase 4).
       - **4-value `<position>` edge-offsets + `background-origin` + `background-clip` — DONE
         (edge-offset / bg-origin / bg-clip cycles):** the shared
         `FragmentPainter.TryParseBackgroundPosition` (used by `object-position` +
         `background-position`) parses the §3.6 THREE-/FOUR-value edge-offset form — an edge
         keyword each optionally followed by a `<length-percentage>` offset FROM that edge
         (`left 10px top 5px`, `right 25% bottom 50%`); components assigned to axes by keyword
         (left/right → X, top/bottom → Y, center → the free axis), either order; two same-axis
         edges / leftover tokens reject. FACADE-reachable only via MARGIN-BOX backgrounds (raw
         values) — a BODY `background-position` or `object-position` 4-value form is
         AngleSharp-dropped at parse (a documented beta gremlin; only the parser + the margin-box
         raw path see it). A body block's `background-image` honors `background-origin` (the
         POSITIONING area — `border-box` / `padding-box` [initial] / `content-box`, the border box
         inset by the USED border / border+padding via new `BackgroundAreaInset` +
         `UsedBorderEdgeWidthPx`, matching the border painter — replaces the border-box
         approximation; borderless boxes byte-identical) and `background-clip` (the PAINT/clip rect
         — `border-box` [initial] / `padding-box` / `content-box`, the tiler's new optional
         clip-rect params bounding the loop clip + the pattern fill). STILL DEFERRED: MARGIN-BOX
         `background-origin`/`-clip` (the band stays the area); the BODY + `object-position`
         4-value position forms (AngleSharp drop); `background-attachment`; gradients (Phase 4).
       - **Container vertical padding + §4.3-gated borders + `object-position` +
         `background-repeat: space`/`round` — DONE (container-vpad / object-position /
         space-round cycles):** a running-element CONTAINER's VERTICAL border+padding now
         BLOCK the §8.3.1 margin collapse (ADD instead of max-collapse) and extend its
         decoration band over its padding strip (container-vpad cycle): `FoldContainerBoxModel`
         returns the boundary-gap parts INSIDE the band (`Leading/TrailingInsidePx` = border +
         padding + the pre-fold inner gap), threaded through `RunningContainer` →
         `PageMarginBoxPainter`'s `ContainerBand` so the band's Y range extends (top − Leading,
         bottom + Trailing); its HORIZONTAL border+padding join the inset propagation into
         descendants. Border widths are §4.3-GATED (new `CaptureSegmentBorderWidths` /
         `GatedBorderWidthPx`: a `border-<side>-style` of `none`/`hidden`/unset → 0;
         `thin`/`medium`/`thick` → 1/3/5; an unset width on a painting edge → the `medium`
         default). `object-position` positions the object-fit-fitted `<img>` content in its
         content box (object-position cycle, CSS Images 3 §5.6): the RAW winner rides `ImgSpec`
         (the property stays UNREGISTERED — a 2-component position needs a new metadata type, a
         documented seam), `ImagePainter` reuses `FragmentPainter.TryParseBackgroundPosition`
         (the §3.6 grammar), unset → centre (byte-identical to the pre-cycle 50% 50%).
         `background-repeat: space`/`round` are the two remaining modes (space-round cycle, CSS
         B&B §3.2): new `BackgroundRepeatMode` (per-axis) + `AxisTilingPlan` (`space` =
         floor(area/tile) tiles flush to the edges with the leftover as equal gaps folded into
         the ORIGIN STEP; `round` rescales the tile so a whole number fits) drive both the loop
         and the pattern path; `RegisterTilingPattern` gained optional `xStepPt`/`yStepPt` (a
         `space` gap makes the step EXCEED the BBox — legal §8.7.3.1 — quantized + in the dedup
         key + on `/XStep`//`/YStep`). STILL DEFERRED: container WIDTH (sub-box wrap) +
         inline-level spans; `object-position` REGISTRATION (a `<position>` metadata type — it
         renders from the raw winner, so `@supports (object-position: …)` does NOT report it; PR
         #169 review P2) + edge-offset (4-value) forms; `background-origin`/`-clip`/`-attachment`;
         gradients (Phase 4).
       - **Container inset propagation + PDF tiling patterns + object-fit — DONE
         (container-insets / tiling-patterns / object-fit cycles):** a running-element
         CONTAINER's horizontal margin+padding now propagate into its DESCENDANTS
         (container-insets cycle): every descendant segment's line band + glyphs/extent inset
         by the container's content-box offset (folded into the segment margin slots — the
         leaf's own band moves too), and every NESTED container band insets under the outer's
         content box (the outer's own band keeps its margin-only inset — its padding is inside
         its band); runs for every recursed container, decorated or not. Background-image
         tilings ABOVE the 16-tile per-fragment loop threshold emit ONE PDF tiling-pattern
         fill (tiling-patterns cycle, ISO 32000-2 §8.7.3): `PdfDocument.RegisterTilingPattern`
         (PatternType 1, the cell paints the image, the grid phase baked into `/Matrix` —
         pattern space anchors to DEFAULT user space §8.7.3.1, deduped by image/size/anchor) +
         `PdfPage.FillRectangleWithPattern` (`/Pattern cs … scn … re f`, per-object resource
         dedup); the fill rect clamps per NON-repeating axis; the old 4096-tile cap +
         `PAINT-BG-IMAGE-TILE-CAP-001` are REMOVED (unreachable — O(1) for any count); at or
         below the threshold the per-tile loop stays byte-identical. `object-fit` fits an
         `<img>`'s content in its content box (object-fit cycle, CSS Images 3 §5.5): `fill`
         (initial — byte-identical), `contain`/`cover` (aspect-preserving), `none` (intrinsic),
         `scale-down` (min of none/contain), all CENTRED (the `object-position` 50% 50%
         initial), an overflowing concrete size (`cover`/`none`) clipped at the content box;
         an unknown raw falls back to `fill` (AngleSharp drops invalid keywords upstream — the
         painter fallback is defense-in-depth). STILL DEFERRED: container BORDER widths (need
         the §4.3 style gate) + VERTICAL padding (band extension) + width (sub-box wrap) +
         inline-level spans; `object-position` (non-center); `space`/`round` repeats;
         edge-offset positions; `background-origin`/`-clip`/`-attachment`; gradients (Phase 4).
       - **element() nested CONTAINER bands + margin-box background-image + background
         position/size/repeat — DONE (container-bands / margin-box-bg-image / bg-variants
         cycles):** a DECORATED intermediate block between the running root and the leaf lines
         paints ONE band spanning its descendants' lines (PRE-order capture — an outer
         container paints under an inner one; the Y range rides the per-line geometry, a
         vertical truncation clamps it); its VERTICAL margins fold into the boundary segments'
         gap margins at capture (max-collapse, §8.3.1's parent/first-last-child case —
         decorated or not), and its own horizontal margins inset ITS band only. A page margin
         box's `background-image: url(...)` tiles over its band (the raw-read pattern; margin
         boxes resolve EARLY so the prefetch sees their urls — the PrintBackgrounds prefetch
         gate applies; initial repeat/auto/0%0% only; rectangular tiles over a rounded band —
         documented). Body `background-repeat` (4 keywords + the two-value axis form; a
         repeating axis covers the area at the position's PHASE, a non-repeating axis paints
         ONE clipped tile), `background-size` (auto / contain / cover / `<length|%>{1,2}` with
         aspect completion) and `background-position` (keywords incl. the swapped pair,
         absolute lengths, the §3.6 percentage rule; one value → other axis centers) drive the
         shared tiler — AngleSharp-beta EXPANDS repeat/position into `-x`/`-y` longhands, so
         the capture recomposes the two-value form. An unsupported form (`space`/`round`,
         3-/4-value positions, non-absolute units) surfaces once + that longhand falls back to
         its initial WHOLE (no half-applied axes). Margin-box VARIANTS are wired too
         (post-PR-#167 review P1 — the raw winners ride `MarginBoxBackgroundImage` into the
         shared tiler; margin-box bodies never pass through AngleSharp, so no `-x`/`-y`
         recompose). STILL DEFERRED: container width/padding affecting CHILD line geometry
         (sub-box wrap) + inline-level spans; `background-origin`/`-clip`/`-attachment` (the
         positioning area stays the BORDER box — documented); `space`/`round`; edge-offset
         positions; PDF tiling patterns (the O(1) tile-cap replacement); gradients (Phase 4).
       - **Body image pipeline + background images + per-line horizontal margins — DONE
         (img-pipeline / bg-image / segment-hmargins cycles):** the BODY IMAGE PIPELINE is
         live — a BLOCK-level `<img>` renders end-to-end: every image reference is prefetched
         BEFORE layout through `SafeResourceLoader` (`data:` URIs decode INLINE with no user
         loader — the self-contained default; the parser's Phase-A `img[src]` data:-strip now
         EXEMPTS allowlisted IMAGE mediatypes, validated downstream by the MIME allowlist +
         magic-byte decode; `file:`/`http(s):` via `HtmlPdfOptions.ResourceLoader` under
         `SecurityPolicy`), decoded once per URI (PNG/JPEG passthrough; GIF/WebP/BMP via the
         SkiaSharp raster fallback) into a registered XObject (per-URI memo + content-hash
         dedup); `ReplacedSizeResolver` writes the §10.3.2 used size into the slots (CSS
         declared > HTML dimension attrs > intrinsic 1:1 px; aspect-ratio completion from an
         ABSOLUTE other side) so layout sizes it like an explicit block (`margin: auto`
         centres); `ImagePainter` places the content-box rect after bands/borders, before
         text. `background-image: url(...)` on body blocks TILES the decoded image over the
         border box (initial `repeat`; clipped partial edge tiles; over the color, under
         borders; `PrintBackgrounds`-gated; 4096-tile cap → `PAINT-BG-IMAGE-TILE-CAP-001`).
         A leaf block's own horizontal MARGINS inset its line's per-line BAND + glyphs/extent
         (margins sit OUTSIDE the border box — padding stays inside the band; absolute only,
         clamped ≥ 0). Failures surface `RES-LOAD-FAILED-001` / `IMG-DECODE-FAILED-001` /
         `CSS-BACKGROUND-IMAGE-UNSUPPORTED-001` — nothing drops silently. STILL DEFERRED:
         INLINE `<img>` (the atomic-inline deferral — no line-box atomics yet), `object-fit`,
         `%` dimension attributes, `srcset`, ICC profiles, SVG sources (needs the sanitize/
         rasterize pipeline — the MIME allowlist rejects `image/svg+xml`), gradients +
         multi-layer backgrounds (Phase 4 shading patterns), `background-position`/`-size`/
         `-repeat` variants (the tile phase starts at the BORDER box — spec phases at the
         padding box), PDF tiling-pattern objects (the O(1) replacement for the tile cap),
         MARGIN-BOX background images, % `height` relative-resolution for replaced boxes,
         negative / `auto` per-line horizontal margins (no per-line centering distribution),
         real nested block LAYOUT.
       - **Horizontal per-line padding + float % lengths + body box-sizing — DONE
         (hpadding / float-percent / box-sizing cycles):** a leaf block's own absolute
         `padding-left/right` insets its line's glyphs + alignment extent (the band keeps the
         full width; the wrap width isn't narrowed per segment — a padded long line clips via
         the clip-path safety net). Float `width`/`margin-*`/`padding-*` percentages resolve
         against the BFC content box (float `% height` deferred; abspos % was already live).
         An explicit body width under `box-sizing: border-box` IS the border box (floored at
         the insets), at both the block and inline-only paths. STILL DEFERRED THEN: per-line
         horizontal margins + background images (body image pipeline) — BOTH shipped in the
         NEXT entry (img-pipeline / bg-image / segment-hmargins cycles, see above) — and real
         nested block LAYOUT.
       - **Per-line segment padding + body % height + margin:auto centering — DONE
         (segment-padding / percent-height / auto-margins cycles):** a leaf block's own absolute
         VERTICAL padding grows its line's band/pitch (background covers the padding box;
         horizontal per-line padding deferred — needs per-line X insets). `height: N%` resolves
         against a DEFINITE containing height (fragmentainer at the outer level; the parent's
         resolved content height threaded through the recursion — an auto parent computes to
         auto per §10.5, browser-equivalent). `margin-left/right: auto` with an explicit width
         distributes per §10.3.3 (centred / one-sided / over-constrained-clamped), incl. the
         inline-only path. STILL DEFERRED: horizontal per-line padding, floats/abspos %,
         `% height` on floats, real nested block LAYOUT.
       - **Body % lengths + per-line segment margins + per-segment line-height — DONE
         (body-percent / segment-margins / segment-line-height cycles):** body `width`/`margin-*`/
         `padding-*` PERCENTAGES resolve at layout time against the containing block's INLINE size
         (CSS 2.2 §10.2/§8.3/§8.4; % padding is rewritten to used px in place so paint agrees;
         still deferred THEN: floats/abspos %, % in body calc() — `% height` and `margin: auto`
         shipped in the NEXT cycle, see the entry above). A leaf block's own ABSOLUTE vertical margins insert collapsed
         inter-line gaps (max of adjoining, floored at 0; %/relative/`auto` read 0); its own
         `line-height` (absolute, unitless, or em) drives its line's pitch (`normal`/%/other →
         font × 1.2). STILL DEFERRED: per-line padding, real nested block LAYOUT.
       - **element() segments part 2: per-line decoration + text-align + pitch — DONE
         (segment-decor / segment-align / segment-pitch cycles):** a leaf block's own
         background/border paints a per-LINE band behind its line; a segment's own `text-align`
         aligns its line (the box's declared text-align wins); each line advances by ITS segment's
         pitch (font × 1.2 — replaces the max-font uniform approximation; cumulative tops,
         per-line half-leading, per-line truncation). STILL DEFERRED: per-line padding,
         real nested block LAYOUT (separately laid-out sub-boxes). (Per-line vertical MARGINS +
         per-segment LINE-HEIGHT shipped in the NEXT cycle — see the entry above.)
       - **Box/element separately-decorated + margin-box border-radius + content(before|after) — DONE
         (nested-decor / border-radius / content-pseudo cycles):** a co-declared standalone element()'s
         decoration paints as a NESTED band at its content block (box band at the box rect; element-only
         decoration keeps the coinciding band; per-side inset re-attribution deferred). A single uniform
         absolute `border-radius` rounds the margin-box BACKGROUND band (`PdfPage.FillRoundedRectangle`,
         kappa Béziers, half-min clamp; %, per-corner, elliptical, relative/calc(), body path, and
         rounded border STROKES surfaced/deferred). `content(before|after)` resolves the host's pseudo
         `content` raw as a plain content-list (missing/none/normal → empty; first-letter/marker stay
         the unsupported bail). The cycle also fixed a REAL adapter bug: a rule AngleSharp drops
         ENTIRELY now synthesizes from its preprocessor recovery instead of demoting to opaque (which
         lost the recovery and desynced recovery ordinals).
       - **Body explicit `width` (post-PR-#159 handoff-spotted gap) — first cut DONE:** an in-flow
         `BlockContainer`/`ListItem` with an explicit `width` sizes its border box to
         width + inline borders + padding at BOTH `BlockLayouter` fill sites (outer dispatch +
         subtree recursion, shared `ResolveInFlowBorderBoxInlineSize`), mirroring the inline-only
         block path — so an empty `width: 64px` div's background band no longer spans the full
         content width. STILL DEFERRED (the CSS 2.2 §10.3.3 remainder): margin DISTRIBUTION
         (`margin: auto` centering, the over-constrained rule — the box keeps its inline-start
         edge), body `box-sizing`, percent width (reads as 0 → auto fill, the
         `ReadLengthPxOrZero` cycle-1 contract), and the explicit width of `Table`/`InlineTable`
         wrappers (the measured-grid growth logic owns their inline extent),
         `FlexContainer`/`GridContainer`, and replaced boxes (all keep the documented
         available-range fill).
       - **`@page :first` selector (cycle 10) — DONE:** `@page :first` rules apply on the single
         (first) page, overriding the bare `@page` by cascade specificity — `AtPageRules.EnumeratePageRules`
         yields bare-then-`:first` so the resolvers' last-wins cascade lets `:first` win (a bare
         `!important` still beats a `:first` normal).
       - **`@page :left`/`:right`/`:blank` selectors (multi-page driver cycle 6) — MARGIN BOXES DONE,
         GEOMETRY DEFERRED:** for MARGIN BOXES (running headers/footers) these now apply per page — the
         driver builds an `AtPageRules.PageSelectorContext(pageIndex, IsBlank)` (the LTR parity: page 0 =
         recto/right, alternating; a body-fragment-less page is `:blank`), and `AtPageRules.MatchTier` (the
         page-context generalization of `ClassifyPageSelector`) picks the applicable `@page` rules in CSS
         Page 3 §3.1 specificity order (bare < `:left`/`:right` < `:first`/`:blank` < `@page <name>`), which
         the per-page `AtPageMarginBoxResolver.Resolve(ctx)` paints. NAMED pages (cycle 7 + PR #179 review)
         also select MARGIN BOXES per page AND FORCE a page break: the `page` property (`auto | <custom-ident>`,
         dropped by AngleSharp → recovered by `CssPreprocessor`, read raw) is resolved by
         `AtPageRules.ResolveUsedPageName` (§3.4 used value — nearest VALID-`<custom-ident>` ancestor; CSS-wide
         keywords + invalid raws like `-1` resolve to the parent, NOT literal names — PR #179 P1) and stored on
         `Box.PageName` at build time. `BlockLayouter` FORCES a page break before a block-flow child whose
         `Box.PageName` differs from the preceding one (CSS Page 3 §3.4 — so a named section that FITS the
         current page still starts a new one); the driver reads each page's name from its first content box's
         `Box.PageName` (skipping the `<html>`/`<body>` wrappers) and `MatchTier` matches a bare
         `<custom-ident>` `@page` selector at tier 3. STILL DEFERRED: (a) per-page GEOMETRY — a
         `@page :left`/`:right`/`<name>` that changes `margin`/`size` would reflow LAYOUT per page (the content
         box differs), which needs an iterative layout pass (the margin/size resolvers stay single-page, bare +
         `:first`); (b) COMPOUND selectors (`:first:left`, `chapter:first`) → `MatchTier` returns no-match
         (`DeclaredPageNames` still collects a compound's leading name for the union); (c) `:blank` is
         implemented but latent — the driver doesn't yet emit mid-document blank pages (no forced parity
         breaks); (d) `page` is not a registered first-class property (no `@supports` — recovery + raw read,
         but CSS-wide/`<custom-ident>` validation IS applied at use). The forced break compares ADJACENT
         block-flow SIBLINGS (a first cut of §3.4's "preceding box"). RTL parity flip out of scope. `calc()` /
         font-relative margin units also deferred (absolute lengths + percentages are done).
  4. **Deterministic default font** — `SystemFontResolver` reads platform
     fonts (non-deterministic); a bundled last-resort font is needed for
     the determinism contract (CLAUDE.md rule #4) once PDFs are emitted.
  5. **Async font pre-resolution** (post-PR-#117 review P1) — shaping is
     synchronous, so `HarfBuzzShaperResolver` fails fast on a
     non-synchronous `IFontResolver` (e.g. a CDN fetch). A layout pre-pass
     that resolves all needed faces async + warms the cache off-thread
     would let async resolvers work without blocking layout.
  6. **Per-size pinned-font memory** (post-PR-#117 review P3, perf
     follow-up) — the shaper cache keys on size + each `HbShaper` copies +
     pins the full font bytes, so a document with many computed sizes
     duplicates the payload. Optimization: a blob cache keyed by validated
     font identity (pin once) + size-specific HarfBuzz `Font` objects over
     the shared blob.
- **Trigger** — end-to-end HTML→PDF output; the invoice corpus render
  (Phase 3 Task 28).
- **Owner files** —
  - `src/NetPdf/HtmlPdf.cs` (facade — wired cycle 2) +
    `src/NetPdf/Rendering/` (`PdfRenderPipeline`, `FragmentPainter`,
    `ListFragmentSink`, `PdfUnits` — cycle 2) +
    `src/NetPdf/Shaping/HarfBuzzShaperResolver.cs` (cycle 1).
  - `src/NetPdf/Diagnostics/` (`CollectingDiagnosticsSink`,
    `PaginateToPublicDiagnosticsAdapter` — cycle 2).
  - `src/NetPdf.Css/ComputedValues/PropertyResolvers/PropertyResolverDispatch.cs`
    (font-size + line-width resolvers — TODO 1).
  - `src/NetPdf.Pdf/PdfPage.cs` (`FillRectangle` — cycle 2) +
    `src/NetPdf.Pdf/` (the byte writer — complete).
  - `src/NetPdf.Paint/` (`DisplayCommand` IR → PDF consumer — future TODO 2).
- **Added** — Phase 5 layout→PDF wiring cycle 1; cycle 2 wired the facade
  + background paint bridge. Cycle 5a-2-ii added the `TextPainter` (real glyphs
  end-to-end) + `HarfBuzzShaperResolver.ResolveFontProgram` + owner file
  `src/NetPdf/Rendering/TextPainter.cs`.
- **Removal condition** — `HtmlPdf.Convert` renders a real text document
  to a valid PDF `byte[]` end-to-end. **Substantially met** (cycle 5a-2-ii:
  real glyphs paint + embed deterministically with a fixed font); the DEFAULT
  facade path still depends on platform fonts until the bundled fallback (5b),
  so the determinism contract for the default path remains open.
