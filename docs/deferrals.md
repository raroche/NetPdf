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

## word-break-keep-all-cjk

- **ID** — `word-break-keep-all-cjk`
- **Status** — `approximated` (uniform), `throws` (mismatch).
- **Behavior** — Uniform `word-break: keep-all` silently behaves like
  `normal` (no break suppression). A `keep-all` ↔ {`normal`, `break-all`}
  mismatch across source TextRuns throws
  `NotSupportedException` from `InlineLayouter.LayoutPerRun`.
- **Missing** — UAX #24 per-codepoint script detection + UAX #14 LB30b
  ("do not break between two ID-class characters when `word-break:
  keep-all`") to suppress CJK inter-character breaks.
- **Trigger** — corpus adds CJK content (Chinese / Japanese / Korean
  invoices, reports, books), OR a user-reported failing case.
- **Owner files** — `src/NetPdf.Text/Bidi/UnicodeScripts.cs` (new — needs
  the script table); `src/NetPdf.Layout/Inline/LineBuilder.cs` flat-build
  phase (read script per glyph + suppress LB30b boundaries); remove the
  `KeepAll` mismatch throw in
  `src/NetPdf.Layout/Inline/InlineLayouter.cs::LayoutPerRun`.
- **Added** — Phase 3 Task 10 cycle 3d sub-cycle 3 (added the mismatch
  throw); cycle 3b sub-cycle 2 (added the uniform approximation).
- **Removal condition** — UAX #24 script detection lands AND the wrap
  loop honors KeepAll per glyph AND
  `InlineLayouter.LayoutPerRun` no longer throws on KeepAll mismatch.

---

## white-space-break-spaces

- **ID** — `white-space-break-spaces`
- **Status** — `approximated`.
- **Behavior** — Both the preprocessor and the wrap pass treat
  `white-space: break-spaces` identically to `pre-wrap` (preserve all
  whitespace + wrap at UAX #14 Allowed opportunities only).
- **Missing** — Per CSS Text L3 §6.4, `break-spaces` must add forced wrap
  candidates at **every** preserved space glyph (not just UAX #14 Allowed
  positions) AND honor trailing-space wrap-vs-hang at line ends.
- **Trigger** — corpus needs `break-spaces` semantics (rare; mainly
  legal/typographic content with explicit trailing-space wrapping).
- **Owner files** —
  `src/NetPdf.Layout/Inline/LineBuilder.cs` flat-build phase (synthesize
  Allowed at every SP glyph in `break-spaces` source runs) + wrap loop's
  trailing-trim path (hang trailing SPs past the line edge instead of
  trimming).
- **Added** — Phase 3 Task 10 cycle 3 review (User #3) added the enum
  value with the documented PreWrap-equivalent approximation.
- **Removal condition** — wrap pass synthesizes per-glyph forced
  candidates inside `break-spaces` runs AND trailing-space hang/wrap
  semantics are implemented.

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

## uax-24-script-detection

- **ID** — `uax-24-script-detection`
- **Status** — `approximated`.
- **Behavior** — `LineBuilder.Itemize` accepts a single ISO 15924 script
  tag + BCP 47 language passed uniformly from the caller. Multi-script
  paragraphs (Latin + Arabic + Han in one `<p>`) all shape with the same
  feature set.
- **Missing** — Per-codepoint script detection (UAX #24) producing a
  script-change boundary in `Itemize`, so each script-homogeneous
  sub-run gets its own shaping call with the appropriate OpenType
  feature set.
- **Trigger** — mixed-script content enters the corpus, OR a user-reported
  failing case where script-specific shaping features (e.g., Arabic
  joining contextual forms across a Latin↔Arabic boundary) misrender.
- **Owner files** — `src/NetPdf.Text/Bidi/UnicodeScripts.cs` (new — see
  the `word-break-keep-all-cjk` entry; same table);
  `src/NetPdf.Layout/Inline/LineBuilder.cs::Itemize` (insert
  script-change boundaries).
- **Added** — Phase 3 Task 10 cycle 1 documented the deferral.
- **Removal condition** — `Itemize` produces script-typed itemized runs
  and `Shape` consumes them with the script-specific HarfBuzz feature
  set.

---

## rtl-fragment-reversal

- **ID** — `rtl-fragment-reversal`
- **Status** — `approximated`.
- **Behavior** — HarfBuzz shapes RTL runs in visual (reversed) order
  internally; `LineFragment.Slices` stays in document order. Painters
  walk per-shaped-run glyph arrays as-is. Single-direction LTR
  paragraphs paint correctly. Single-direction RTL paragraphs walk in
  the wrong visual order at the fragment level.
- **Missing** — Fragment-level slice reversal for RTL paragraph base
  direction so the painter consumes slices visually right-to-left.
- **Trigger** — RTL primary direction (Arabic / Hebrew) enters the
  corpus, OR a user reports right-to-left mis-rendering.
- **Owner files** — `src/NetPdf.Layout/Inline/LineBuilder.cs::Wrap`
  emission site (`EmitDrawableRange`) — reverse slice order when the
  paragraph base direction is `ParagraphDirection.RightToLeft`.
- **Added** — Phase 3 Task 10 cycle 1 documented the deferral.
- **Removal condition** — Wrap reverses slice order for RTL paragraphs
  AND the painter consumes them visually.

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
- **Status** — `approximated` (skip + diagnostic).
- **Behavior** — `BlockLayouter.CollectInlineTextRuns` recognizes
  inline-level atomic boxes (`BoxKind.InlineBlockContainer`,
  `InlineFlexContainer`, `InlineGridContainer`, `InlineTable`,
  `InlineReplacedElement`) but currently SKIPS their content. Each
  skipped occurrence emits the
  `LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001` diagnostic (Warning) so
  callers see the gap rather than silently mis-rendering.
- **Missing** — Per CSS Inline L3, atomic inline boxes participate
  in the inline formatting context as opaque units: their intrinsic
  width/height contributes to line-box advance + line-box block
  extent, the surrounding text shapes around them, and they're
  positioned on the baseline (or whatever `vertical-align`
  resolves to). The line builder needs an "atomic inline glyph"
  primitive carrying box-fragment width + ascent/descent so wrap
  decisions account for it. Replaced elements specifically need
  intrinsic-sizing via the existing `ImageSafetyValidator` /
  `FontSafetyValidator` resolved intrinsic dimensions.
- **Trigger** — corpus needs inline-block / inline-replaced
  content (typical use case: `<img>` inline in a paragraph, or
  `display: inline-block` styled spans), OR a user-reported case
  where atomic inline content disappears.
- **Owner files** —
  - `src/NetPdf.Layout/Inline/LineBuilder.cs` — define an "atomic
    glyph" primitive (or extend `ShapedGlyph` with an atomic-box
    payload) that wrap decisions treat as a single non-breakable
    unit with its own advance + ascent/descent.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs::CollectInlineTextRuns`
    — convert each atomic inline box into the new primitive
    instead of emitting the warning + skip.
  - `src/NetPdf.Layout/Inline/InlineLayouter.cs::LayoutPerRun` —
    pass atomic primitives through to `LineBuilder.Wrap`.
- **Added** — Phase 3 Task 11 sub-cycle 1 review Finding #4
  (this branch).
- **Removal condition** — `CollectInlineTextRuns` no longer emits
  `LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001`; atomic inline boxes
  participate in line layout as opaque advances; a test renders
  a paragraph with an inline `<img>` and the image's geometry is
  preserved in the emitted fragments.

---

## table-auto-fixed-spans-borders

- **ID** — `table-auto-fixed-spans-borders`
- **Status** — `approximated`.
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
  `border-collapse`, no `<thead>` / `<tfoot>` repetition across
  pages, no RTL
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
  diagnostic. **Limitation** — the OUTER `BlockLayouter` child
  loop integrates the continuation propagation, but the
  `EmitBlockSubtreeRecursive` nested-walk path (= the typical
  `<html><body><table>` shape from real HTML) keeps tables
  ATOMIC: the recursion has no continuation-emission route, so
  nested tables emit every row on the same page + rely on the
  existing forced-overflow diagnostic for over-tall cases. Task 13
  cycle 2+ may generalize the recursion. **Sub-cycle 3 — captions (`<caption>`) lay
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
  §6.4 column-group widths beyond Pass A fallback; per-page
  `<thead>` / `<tfoot>` repeat (Task 13 cycle 2); nested-recursion
  table-continuation propagation (Task 13 cycle 1 ships outer-loop
  row splitting + nested tables stay atomic with forced-overflow
  fallback; cycle 2+ may generalize); row-internal splitting (a
  single row taller than the fragmentainer is currently atomic +
  triggers forced-overflow);
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
    nested-recursion path uses the new `NoBreakBreakResolver` to
    keep nested tables atomic — cycle 2+ may generalize).
    Remaining: spec-strict §11 rowspan distribution-proportional
    algorithm; §6.3 border-collapse model + `border-spacing`; per-
    page `<thead>` / `<tfoot>` repetition (Task 13 cycle 2);
    row-internal splitting + row-level `break-inside: avoid`; RTL
    writing modes / row reversal / caption inline-axis keyword
    routing; HTML5 colspan='0'/rowspan='0' remainder semantics;
    percentage column widths.
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
- **Removal condition** — All "Missing" items above are
  implemented: percentage column widths; §6.3 border-collapse +
  border-spacing; per-page header/footer repeat; multi-fragmentainer
  row splitting; §11 spec-strict rowspan distribution-proportional
  algorithm; CSS Tables L3 §3 spec-strict proportional-weight column
  distribution; block-level fixed `width` honoring + replaced-element
  intrinsic-width measurement; HTML5 colspan='0'/rowspan='0'
  remainder semantics; RTL writing modes; HTML width attribute as a
  low-specificity presentational cascade hint.

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
