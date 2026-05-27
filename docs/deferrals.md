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
  §6.4 column-group widths beyond Pass A fallback; row-internal
  splitting (a single row taller than the fragmentainer is
  currently atomic + triggers forced-overflow);
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
    Remaining: spec-strict §11 rowspan distribution-proportional
    algorithm; §6.3 border-collapse model + `border-spacing`;
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
  containing block-level descendants) hosts a nested multicol or
  table whose pagination breaks mid-emission, the `BlockLayouter`
  recursion produces a non-null `LayoutContinuation` for that
  subtree. Floats are out-of-flow per CSS 2.2 §9.5; propagating
  their continuation through the in-flow pagination machinery would
  require float-tracking continuation machinery
  (FloatManager-aware continuation state, float-fragment resume
  contract, BFC-snapshot restoration in the float emission path).
  The cycle 2 hardening pass discards the recursion return inside
  float subtrees (the float's first-page slice is committed; the
  remainder is truncated) + emits the new
  `LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001` Warning diagnostic at most
  once per page so the truncation is observable.
- **Missing** — float-tracking continuation machinery
  (FloatManager state snapshot/restore across pages; float-
  fragment resume contract; cross-page float overflow per
  CSS Fragmentation L3 §5).
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
- **Status** — `approximated`. Phase 3 Task 15 L1 ships single-line
  `flex-direction: row` with `flex-start` packing. Most Flexbox L1
  features are deferred to sub-cycles.
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
  (Auto/Content delegate to declared main-size; LengthPx uses the
  explicit pixel value; Percentage resolves against the container's
  main-size); compute `freeMainSpace = containerMainSize -
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
    text-shaping integration to align item baselines on the cross
    axis (= the cross-axis cursor advances to the line's baseline
    position rather than the line's cross-start, computed from
    HarfBuzz-shaped runs); L8+ scope alongside `align-items: baseline`.
    The cascade slot is lossless so pre-authored
    `align-content: baseline` declarations activate the new behavior
    without a re-author.
  - Writing-mode and `direction` integration for `flex-direction`
    axis mapping (CSS Flexbox §3.1): all 4 directions are honored
    for LTR horizontal-tb but the axis mapping differs in RTL +
    vertical writing modes. For example, `row` in RTL means right-
    to-left along the inline axis (physically equivalent to LTR
    `row-reverse`); `row` in vertical-rl swaps the main + cross
    axes onto the block + inline directions of the rotated writing
    mode. L7+ scope — requires plumbing `direction` +
    `writing-mode` properties through the layout pipeline (L6
    shipped `flex-wrap: wrap` without addressing this gap). Pinned
    by the Skip'd
    `L5_known_gap_rtl_row_should_flip_main_axis_but_no_direction_pipeline_yet`
    test — when L7+ adds the direction pipeline, that test should
    flip to spec-correct expectations + this bullet should be
    removed.
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
  - `align-items: baseline` / `first baseline` / `last baseline`
    (CSS Box Alignment L3 §6.2 + CSS Flexbox L1 §8.3) — requires
    text-shaping integration to align item baselines. L3 decodes
    all three baseline indices (3 / 4 / 5) to the safe default
    `stretch`; sub-cycle L4+ scope. Authoring `baseline` today
    behaves as `stretch` for layout but the cascade still records
    the requested value, so when L4+ ships the deferred behavior
    activates without a re-author.
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
  - **Explicit-width honoring for flex containers**. Per Phase 3
    Task 15 L4 post-PR-#64 review F#2 — a `display: flex;
    flex-direction: column; width: 200px` container in a 600px page
    currently has `_contentInlineSize = 600` (= the available
    inline range from `BlockLayouter`'s float-adjusted derivation
    at BlockLayouter.cs:1138). The FlexLayouter then computes
    `align-items: center` against the 600 page width, not the
    declared 200. The fix touches the BlockLayouter
    width-resolution pipeline (cycle-1 BlockLayouter does NOT
    honor declared `width` as a shrink-to-fit constraint —
    `borderBoxInlineSize` is always the float-adjusted available
    range). Tracked by the
    `L4_hardening_known_gap_column_flex_ignores_declared_width`
    pinning test + the Skip'd
    `L4_hardening_column_explicit_width_smaller_than_page_centers_correctly`
    test. Per Phase 3 Task 15 L6 post-PR-#66 review F#2 — also
    tracked by the
    `L6_hardening_known_gap_narrow_flex_in_wide_page_does_not_wrap_yet`
    production-pipeline test, which pins the wrap-related symptom:
    `width: 250px` declared on a `flex-wrap: wrap` container in a
    600px page → wrap fires against the page width (= 4×100=400 <
    600 → no wrap) instead of the spec-correct declared width
    (= 4×100=400 > 250 → 2 lines). When the BlockLayouter
    width-resolution fix lands ALL THREE tests should flip — at
    which point remove BOTH this bullet AND all three pins.
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
  - **`flex` shorthand parser** (CSS Flexbox L1 §7.4) — the shorthand
    `flex: <flex-grow> <flex-shrink> <flex-basis>` (with sentinel
    values `none` / `auto` / `<number>` per §7.4) is not yet parsed.
    Authors writing `flex: 1` will see the declaration silently dropped
    (AngleSharp.Css doesn't expand the shorthand to longhands either).
    L9+ scope. Workaround until then: use the three longhand
    properties (`flex-grow: 1; flex-shrink: 1; flex-basis: 0`).
  - **`flex-basis: min-content` / `max-content` / `fit-content`**
    intrinsic-sizing keywords (CSS Flexbox L1 §7.2 + CSS Sizing L3
    §5.1). L8 admits `auto` + `content` + `<length-percentage>` only;
    the three intrinsic keywords are L9+ scope (depend on intrinsic
    sizing integration with the BlockLayouter pre-measure).
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
  - **`flex-basis: content` proper implementation** (CSS Flexbox L1
    §7.2.1): post-PR-#68 F#4 — currently `Content` is approximated as
    `Auto` (= delegate to declared main-size). Per spec, Content should
    force the intrinsic content size REGARDLESS of declared
    width/height; e.g., `width: 200; flex-basis: content` should
    produce hypothetical = intrinsic content size (NOT 200). Requires
    intrinsic-sizing integration with the BlockLayouter pre-measure
    (= same prerequisite as `min-content` / `max-content` /
    `fit-content` flex-basis keywords + the §9.7 step-4 min-clamp).
    Pinned by `L8_hardening_known_gap_flex_basis_content_approximates_to_auto`.
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
    approximated via the **L19 declared-dimension contribution**
    (`GridSizing.ItemOuterContribution`): each item contributes
    its explicit width/height + border + padding + margin if
    declared, otherwise contributes 0. Same approximation surface
    as flex `min-width: auto`.
- **NOT in cycle 3** — explicitly deferred so the narrowed scope
  doesn't drift:
  - **True intrinsic content measurement (L19)** — running a
    sub-BlockLayouter dry-run to obtain per-item
    min-content/max-content from rendered descendants. The L19
    approximation above is the placeholder until L19 ships.
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
