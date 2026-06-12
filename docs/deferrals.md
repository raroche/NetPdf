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
- **Status** — `approximated`. Phase 3 Task 17 cycle 3 + cycle 4
  + post-PR-#95 review H6.
- **Behavior** — `GridSizing.ItemOuterContribution` always adds
  the item's border + padding + margin to its declared
  width/height when contributing to intrinsic track sizing. This
  is correct for the CSS default `box-sizing: content-box` but
  WRONG for `box-sizing: border-box` where the declared
  width/height already includes border + padding (= we
  double-count by adding chrome again).
- **Missing** — read `PropertyId.BoxSizing` in
  `ItemOuterContribution` + short-circuit the chrome adds for
  `border-box` items. The fix is local to GridSizing but the
  broader `box-sizing: border-box` support is cross-cutting —
  BlockLayouter + FlexLayouter + TableLayouter all have similar
  declared-vs-rendered-size issues that should be addressed
  symmetrically. Tracked as a single cross-cutting task rather
  than per-layouter patches.
- **Trigger** — a dedicated `box-sizing` pass that audits every
  declared-dimension reader across all layouters + introduces a
  shared `Box.UsedWidth(boxSizing)` / `Box.UsedHeight(boxSizing)`
  helper.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` —
    `ItemOuterContribution`.
  - (Eventually) `src/NetPdf.Layout/Boxes/Box.cs` or a new
    `BoxSizingHelper.cs` for the shared used-size logic.
- **Practical impact** — items using `box-sizing: border-box`
  (the modern norm for reset stylesheets like Bootstrap) over-
  count chrome in intrinsic tracks, causing auto/min-content/
  max-content tracks to size larger than the spec dictates.
  Visible only when an author mixes intrinsic-tracked grids
  with declared-width/height items that have non-zero borders
  or padding.
- **Added** — Phase 3 Task 17 cycle 3 (initial known-gap noted
  in `ItemOuterContribution` xmldoc); post-PR-#95 review H6
  formalized as a deferral entry.
- **Removal condition** — cross-cutting box-sizing audit ships +
  GridSizing reads BoxSizing.

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

## grid-wrapper-rollback-for-pre-dispatch-deferral

- **ID** — `grid-wrapper-rollback-for-pre-dispatch-deferral`
- **Status** — `approximated` (mechanism shipped + verified by 7
  `Cycle5c2a_F1_*` unit tests; production activation pending cycle
  5c.2b clamp reactivation). Phase 3 Task 17 cycle 5c.2a ships the
  deferral's option (a) — pre-dispatch row-fit decision owned by
  `BlockLayouter`.
- **Behavior** — `BlockLayouter` emits the grid wrapper fragment
  BEFORE invoking `DispatchGridInner` (= the wrapper paint order
  contract). If `GridLayouter` were to return
  `PageComplete(GridContinuation(0, null))` under
  `LayoutAttemptStrategy.Strict` signaling "defer the entire grid",
  the wrapper would already be committed on the prior page —
  visually painting an empty grid box.
- **Resolution (cycle 5c.2a)** — chose the deferral's option (a) =
  pre-dispatch row-fit decision OWNED by `BlockLayouter`. New
  private helper
  `PreMeasureGridRowExtentAt(box, rowIndex, ct)` returns the
  resolved height of the next-to-emit row via
  `GridSizing.Resolve`'s dry-run pattern (mirrors
  `PreMeasureGridRowExtent`). At the outer-site Continue path
  BEFORE the wrapper emit, when:
    1. `IsPaginatableGrid(child)` (= every grid container today;
       cycle-5b predicate), AND
    2. `strategy != LayoutAttemptStrategy.LastResort` (= preserve
       the §4.4 progress rule on the final retry), AND
    3. <code>pageRemainingForGridContent
       &lt; fullPageRemainingForGridContent</code> (= progress
       guard; on a fresh page, deferral can't help so don't
       defer), AND
    4. <code>firstRowExtent &gt; pageRemainingForGridContent</code>
       AND <code>firstRowExtent
       &lt;= fullPageRemainingForGridContent</code> (= row
       would fit on a fresh page but not on remaining),
  …`BlockLayouter` routes
  `PageComplete(BlockContinuation(ResumeAtChild=childIdx,
  LayouterState=GridContinuation(RowIndex=startRow,
  Cache=incomingGridForProbe?.Cache, EmittedBlockExtent=0)))`
  without emitting the wrapper. The
  <c>strategy != LastResort</c> gate ensures the §4.4 force-emit
  contract still applies under LastResort (= last attempt; commit
  anyway). The progress guard ensures the deferral is productive
  — a fresh page would fit the row.
- **Sink rollback NOT chosen** — the deferral's alternate option
  (= emit wrapper speculatively + roll back via
  <c>IFragmentSink.RollbackTo</c>) was rejected as having less
  clean semantics. Pre-dispatch query introduces NO speculative
  emission, requires no new sink mutation contract, and reuses
  the existing `GridSizing.Resolve` dry-run pattern. Trade-off:
  the probe duplicates the §11 sizing pass (also done in
  `PreMeasureGridRowExtent` + the actual dispatch); the
  duplicate work is acceptable given §11 sizing is cheap
  relative to item placement + emission.
- **Practical impact (post-PR-#99 review P2#1 correction)** — the
  F1 mechanism is **ACTIVE at the outer-site
  `BlockLayouter.AttemptLayout` contract level**, not dormant.
  Direct-construction callers — tests, integration harnesses,
  any future driver that places a paginatable grid as a direct
  outer-site child of <c>BlockLayouter._rootBox</c> with a tight
  page-remaining geometry — observe the F1 defer routing
  immediately. PR-#99's `Cycle5c2a_F1_*` unit tests prove this.
  Production HTML fixtures, however, route grid containers
  through the recursive <see cref="EmitBlockSubtreeRecursive"/>
  emission path (= the `<body><div class="grid">…</div></body>`
  shape hits the recursive site, not the outer site); the
  recursive site keeps cycle-1 atomic dispatch until cycle 5c.2d
  wires it. So production HTML fixtures see no behavior change
  TODAY, but the F1 contract is permanent + observable from this
  ship forward. AOT/JIT byte-parity of existing fixtures is
  PRESERVED (= 2942DD1E…30C3DE7) because those fixtures don't
  reach the outer-site grid path; this should NOT be read as
  "F1 is dormant" — it's accurate confirmation that the
  recursive path's atomic-dispatch contract is unchanged.
  Cycle 5c.2b will reactivate the outer-site clamp + flip
  <c>paginateGridForOuterChild</c>; cycle 5c.2d will wire the
  recursive site, at which point production HTML fixtures with
  multi-row paginatable grids start exercising F1 + F2.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — F1 helper +
    pre-dispatch row-fit check ✓ (cycle 5c.2a).
- **Added** — Phase 3 Task 17 cycle 5b + post-PR-#97 review F1.
- **Updated** — Phase 3 Task 17 cycle 5c.2a (mechanism ships).
- **Updated** — Phase 3 Task 17 cycle 5c.2b (outer-site clamp +
  gate-flip reactivated; F1 fires for auto-height grids).
- **Updated** — Phase 3 Task 17 cycle 5c.2c post-PR-#101
  review P1#3 (F1 also gated by
  <c>paginateGridForOuterChild</c> so F1 only fires when
  dispatch will actually paginate).
- **Updated** — Phase 3 Task 17 cycle 5c.2d (recursive site
  clamp + F2 + propagation wired; F1 initially NOT applied at
  recursive site).
- **Updated** — Phase 3 Task 17 cycle 5c.2d post-PR-#102
  review P1#1 (F1 NOW applied at recursive site too — uses
  <c>childBlockOffset &gt; 0</c> as productivity guard since
  the recursion doesn't have <c>emittedThisAttempt</c>; mirrors
  the outer-site F1 pattern via the existing
  <c>PreMeasureGridRowExtentAt</c> probe). When F1 fires at the
  recursive site, the recursion returns
  <c>BlockContinuation(ResumeAtChild=childIdx,
  LayouterState=GridContinuation(RowIndex=startRow,
  EmittedBlockExtent=0))</c> WITHOUT emitting the wrapper. The
  outer <c>AttemptLayout</c> wraps the chain into PageComplete
  via the existing chain-propagation pattern. Production
  pipeline regression test
  `Cycle5c2d_post_PR_102_P1_recursive_grid_breaks_before_after_sibling`
  pins the behavior: sibling 200 + grid after on 250px page →
  page 1 emits only the sibling + GridContinuation(RowIndex=0)
  in chain + no <c>LayoutGridForcedOverflow001</c>.
- **Removal condition** — RESOLVED end-to-end. F1 mechanism
  shipped at BOTH outer + recursive sites; production-pipeline
  multi-page tests (`Cycle5c2d_post_PR_102_P2_multi_page_resume_
  emits_all_rows_exactly_once` + the break-before regression)
  verify the contract holds across full HTML → cascade →
  BoxBuilder → BlockLayouter flow. Explicit-height grids still
  wait for `grid-explicit-height-paginate-deferral` +
  `grid-fragment-plan-shared-sizing-deferral`.

---

## grid-fragment-extent-emitted-rows-only-deferral

- **ID** — `grid-fragment-extent-emitted-rows-only-deferral`
- **Status** — `approximated` (consumer shipped + verified by 7
  `Cycle5c2b_F2_*` unit tests; explicit-height grids still pending
  F3 in cycle 5c.2c; recursive site + production tests in 5c.2d).
  Phase 3 Task 17 cycle 5c.1 ships the producer side; cycle 5c.2b
  ships the consumer (= F2 wrapper-resize via
  `IBlockFragmentSink.UpdateFragmentBlockSize` + cursor-advance
  using emitted extent + cycle-5b outer-site clamp reactivated
  for auto-height grids).
- **Behavior** — when grid pagination IS active, the wrapper
  fragment paints at the clamped extent (= page budget) and the
  cursor advances by the full clamped extent. But `GridLayouter`
  may emit only K of N rows; the wrapper visually contains empty
  space + the cursor over-advances + cumulative `ConsumedBlockSize`
  inflates. Following block-flow siblings are pushed down by
  invisible space.
- **Cycle 5c.1 SHIPPED (producer side, PR-#98)** — `GridLayouter`
  now exposes <c>LastEmittedBlockExtent</c> as a public property
  populated on EVERY outcome (PageComplete + AllDone + Strict-
  defer), per the PR-#98 review F1 recommendation that the
  current-fragment extent live on a result-level channel
  available to both outcomes (not solely on
  <c>GridContinuation</c>). Value is derived from row-position
  GEOMETRY (= <c>lastEmittedRow.bottom -
  firstEmittedRow.top</c>) per PR-#98 review F3, NOT
  <c>sum(rowSizes)</c> — the former remains correct once
  <c>row-gap</c> / block-axis alignment land. The
  <c>GridContinuation.EmittedBlockExtent</c> field is kept as a
  redundant carrier when a continuation exists; the layouter
  property is the primary source for cycle 5c.2.
- **Cycle 5c.2b SHIPPED (consumer side)** — `BlockLayouter` now
  reads <c>gridLayouter.LastEmittedBlockExtent</c> via
  <c>DispatchGridInner</c>'s new
  <c>out double lastEmittedBlockExtent</c> parameter + sizes the
  wrapper <c>BoxFragment</c> to
  <c>LastEmittedBlockExtent + chrome</c> via the new
  <see cref="IBlockFragmentSink.UpdateFragmentBlockSize"/> sink
  mutation API + advances the cursor by
  <c>marginStart + chrome + LastEmittedBlockExtent + marginEnd</c>.
  The F2 consumer fires when EITHER
  <c>paginateGridForOuterChild</c> is on (= the outer-site clamp
  fired this page) OR <c>incomingGridContinuation</c> is non-null
  (= resuming a previously-deferred grid; the AllDone-on-resume
  case from cycle 5c.1 PR-#98 review F1 needs the wrapper to size
  to the remaining-rows extent, NOT the full grid's natural
  extent). The cycle-5b outer-site clamp + gate-flip
  (<c>paginateGridForOuterChild</c>) is REACTIVATED for auto-height
  grids on this cycle.
- **Practical impact** — paginatable-grid scenarios at the outer
  site now produce visually-correct wrapper sizing + cumulative
  consumed accounting + correct sibling placement on both pages of
  a split grid. AOT/JIT byte-parity of existing fixtures is
  preserved (= production HTML fixtures route through the
  recursive `EmitBlockSubtreeRecursive` path which is unchanged
  until cycle 5c.2d).
- **Trigger** — cycle 5c.2. Coordinates with F1 (pre-dispatch
  row-fit, shipped 5c.2a) + F3 (explicit-height handling, cycle
  5c.2c).
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` — exposes
    `LastEmittedBlockExtent` ✓ (cycle 5c.1).
  - `src/NetPdf.Paginate/LayoutContinuation.cs` — adds
    `GridContinuation.EmittedBlockExtent` field ✓ (cycle 5c.1).
  - `src/NetPdf.Layout/Layouters/IBlockFragmentSink.cs` — new
    `UpdateFragmentBlockSize(cursor, newBlockSize)` ✓ (cycle 5c.2b).
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — wrapper
    BoxFragment resize + cursor advance both use the emitted
    extent ✓ (cycle 5c.2b).
- **Added** — Phase 3 Task 17 cycle 5b + post-PR-#97 review F2.
- **Updated** — Phase 3 Task 17 cycle 5c.1 + post-PR-#98 review
  F1 + F3 (producer side ships).
- **Updated** — Phase 3 Task 17 cycle 5c.2b (consumer side ships).
- **Updated** — Phase 3 Task 17 cycle 5c.2b + post-PR-#100 review
  P1#1 + P1#2 + P1#3 (= nested-context callers opt out of
  pagination via <c>disableGridPagination</c>; F2 cursor advance
  uses <c>topShift</c>; explicit-height grids gated out of the
  clamp until F3).
- **Updated** — Phase 3 Task 17 cycle 5c.2d (recursive site
  wired with clamp + F2 + continuation propagation;
  production HTML fixtures with auto-height paginatable grids
  now split cleanly via the recursive `EmitBlockSubtreeRecursive`
  path).
- **Removal condition** — F3 explicit-height pagination
  ships (= `grid-explicit-height-paginate-deferral` resolves
  via the `grid-fragment-plan-shared-sizing-deferral`) so
  explicit-height grids in production HTML also paginate via
  the recursive site.

---

## grid-fragment-plan-shared-sizing-deferral

- **ID** — `grid-fragment-plan-shared-sizing-deferral`
- **Status** — `not-started`. Phase 3 Task 17 cycle 5c.2b post-
  PR-#100 review P2.
- **Behavior** — auto-height paginatable grids run
  `GridSizing.Resolve` three times per attempted fragment:
  (1) in `PreMeasureGridRowExtent` to grow the wrapper to
  natural extent; (2) in F1's `PreMeasureGridRowExtentAt`
  probe (when no incoming cache present); (3) inside
  `GridLayouter.AttemptLayout` for the actual dispatch. Each
  `Resolve` runs §11 sizing + §8.5 placement; for grids with
  many items + repeat-expanded tracks, this triples the §11
  work per attempt + amplifies the cycle-5 resume cache's CPU
  amortization rationale.
- **Practical impact** — measurable CPU overhead on large
  invoice / report grids; the resume cache hit path on page 2+
  avoids one Resolve (cycle 5c.2a P1#2), but pages where the
  cache is invalidated (= inline-size mismatch, identity
  mismatch) or absent (= first-page) still triple-resolve.
- **Missing** — a shared per-attempt `GridFragmentPlan`
  immutable record carrying row geometry + placements + the
  next-row fit prediction + the emitted-extent inputs, computed
  ONCE per attempt + threaded through pre-measure +
  `PreMeasureGridRowExtentAt` + `DispatchGridInner` so all
  three sites consume the same authoritative resolve. Mirrors
  the cycle-5 resume cache pattern but lives one layer up (=
  per-attempt, not per-resume-cycle).
- **Trigger** — when a benchmark on a large multi-page grid
  shows measurable CPU regression vs cycle 5b atomic dispatch.
  Until benchmarks land, accepted as a known cost since the
  Length-only track tests in cycle 5c.2a/b don't surface it.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — `Result` type
    becomes the shared plan's payload.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `PreMeasureGridRowExtent` + `PreMeasureGridRowExtentAt` +
    `DispatchGridInner` thread the shared plan.
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` —
    `ConfigureEmission` accepts a precomputed plan in lieu of
    running its own `Resolve`.
- **Added** — Phase 3 Task 17 cycle 5c.2b + post-PR-#100 review
  P2.
- **Removal condition** — shared plan lands AND benchmark
  shows ≤ 1× CPU vs cycle 5b atomic dispatch for paginatable-
  grid fixtures.

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

## grid-explicit-height-paginate-deferral

- **ID** — `grid-explicit-height-paginate-deferral`
- **Status** — `approximated` (mechanism shipped end-to-end +
  verified by Cycle5c3_* unit + production-pipeline tests;
  residual approximation lives only in the cursor-advance
  bookkeeping where the ancestor's `marginBoxBlockSizeForCursor`
  reads the projected subtree extent — see *Residual
  approximation* below). Phase 3 Task 17 cycle 5c.3 +
  post-PR-#110 review P1#1+P1#2.
- **Behavior (pre-5c.3)** — grids with explicit `height: 300px`
  style had their natural extent restored by
  `MeasureSubtreeVisualBlockExtent` AFTER the paginatable-grid
  clamp would have shrunk `borderBoxBlockSize`. The resolver
  path saw a 300px chunk on a 250px page → forced-overflow
  branch → atomic grid dispatch (`allowPagination: false`). So
  even with cycle 5b's outer-site wiring active, explicit-
  height grids would never paginate.
- **Cycle 5c.2c INITIAL → REVERTED** — added a subtree-extent
  clamp in `MeasureSubtreeVisualBlockExtent`'s outer
  consumer + removed the cycle-5c.2b post-PR-#100 review P1#3
  `IsHeightAuto(child)` gate. PR-#101 review correctly
  identified that this changed row sizing inputs to
  GridSizing.Resolve — the subtree clamp without
  geometry/budget separation silently corrupted fr / definite-
  height row resolution. Reverted to the cycle-5c.2b-post-
  PR-#100 state: `IsHeightAuto(child)` gates the outer-site
  clamp; explicit-height grids stay atomic.
- **Resolution (cycle 5c.3 + PR-#110 review P1)** — shipped in
  three coordinated changes:
  1. `GridLayouter.ConfigureEmission` gained a `pageBlockBudget`
     parameter that separates "geometry input" (= authored
     container extent passed to `GridSizing.Resolve` for row
     sizing) from "page budget" (= clamped page-remaining
     capacity that drives `ComputePaginatedRowRange`'s row-
     fit cut-off). This fixes the cycle-5c.2c row-geometry
     corruption: fr / definite rows now distribute against
     authored height, while pagination cuts off at page
     budget. The BlockLayouter outer + recursive grid
     dispatch sites capture `authoredBorderBoxBlockSize`
     pre-clamp + thread it as `contentBlockSize` while
     passing the clamped value as `pageBlockBudget`.
  2. `IsHeightAuto(child)` gate is removed from both grid
     clamps; the subtree-extent clamp is re-enabled (= now
     safe because the dual-input separation prevents the
     row-sizing corruption).
  3. Post-PR-#110 review P1#2 — `MeasureSubtreeVisualBlockExtent`
     projects paginatable grid descendants to the fragment
     budget (`min(authoredExtent, fragmentainer.BlockSize)`).
     Without this, an ancestor's break-check saw the grid's
     full authored extent → false BreakHere → forced-overflow
     path → stale `PAGINATION-FORCED-OVERFLOW-001` + an
     empty trailing page from the forced-overflow's
     unconditional `ResumeAtChild = childIdx + 1` return.
     With projection, ancestors' break-checks take the
     Continue path → no stale diagnostic, no empty tail page,
     end-to-end emission returns AllDone cleanly when the
     grid finishes.
- **Residual approximation** — the projection at (3) caps the
  ancestor's `subtreeBlockExtent` at the fragment budget,
  which feeds `marginBoxBlockSizeForCursor` (= ancestor's
  cursor advance after emit). For an explicit-height grid
  that emits FEWER rows than fill the budget (e.g.,
  `height: 200` grid with 2×100 rows on a 150px page emits
  only row 0 = 100), the ancestor advances by the projected
  budget (150) instead of the F2-resized wrapper extent
  (100). The grid wrapper itself ends up at the correct
  emitted extent via the F2 resize; the over-advance lives
  only in the ancestor's UsedBlockSize accounting (= ≤
  page-budget, so the page still closes cleanly without
  spurious PageComplete). Eliminating this residual requires
  a measure→emit→re-measure pass or threading the F2 result
  back through the ancestor's cursor-advance — tracked
  alongside the broader cursor-accounting work in
  `recursive-block-continuation-consumed-extent-accounting-deferral`.
- **Performance follow-on** — the full `GridFragmentPlan`
  consolidation across pre-measure + F1 + dispatch (=
  performance optimization that lets the §11 sizing work
  run ONCE per attempt instead of three times) is still
  tracked under `grid-fragment-plan-shared-sizing-deferral`.
  Cycle 5c.3 explicitly chose the minimum-viable correctness
  fix without the perf consolidation; the perf work is
  independent + post-v1.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` —
    `ConfigureEmission.pageBlockBudget` parameter;
    `_pageBlockBudget` field; budget threading through
    `ComputePaginatedRowRange`.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` —
    `authoredBorderBoxBlockSize` capture at both grid
    dispatch sites; subtree-extent clamp re-enabled;
    `MeasureSubtreeVisualBlockExtentRecursive` projection;
    `IsHeightAuto(child)` gate removed from both clamps;
    F1 probe sites use authored geometry.
- **Added** — Phase 3 Task 17 cycle 5b + post-PR-#97 review F3.
- **Updated** — Phase 3 Task 17 cycle 5c.2c initial (mechanism
  attempted) + post-PR-#101 review P1#1 (mechanism reverted)
  + cycle 5c.3 (dual-input fix shipped, outer-site projection
  pending) + post-PR-#110 review P1#1+P1#2 (outer-site
  projection shipped, deferral resolved end-to-end except
  the residual cursor-advance approximation documented
  above).
- **Removal condition** — RESOLVED end-to-end for
  correctness; the residual cursor-advance approximation
  rolls into `recursive-block-continuation-consumed-extent-
  accounting-deferral` for the broader unified fix. PR-#110
  ships dedicated regression tests:
  `Cycle5c3_explicit_height_grid_with_fr_paginates_with_authored_geometry`
  (production pipeline, 2 pages exactly, no stale
  `PAGINATION-FORCED-OVERFLOW-001`, page 2 outcome AllDone,
  page 2 wrapper at the F2-resized emitted extent),
  `Cycle5c3_explicit_height_with_fr_rows_preserves_authored_geometry`
  (resume cache RowBaseSizes = [100, 300] proving fr
  resolved against authored 400, not clamped 250),
  `Cycle5c3_recursive_explicit_height_grid_paginates` (asserts
  the diagnostic does NOT fire end-to-end through the full
  HTML → cascade → BoxBuilder → BlockLayouter chain).

---

## grid-spanning-item-intrinsic-distribution-deferral

- **ID** — `grid-spanning-item-intrinsic-distribution-deferral`
- **Status** — `approximated`. Phase 3 Task 18 cycle 6a ships
  equal-share distribution; spec-strict §11.5.1 step 3
  distribution-proportional is post-cycle-6.
- **Behavior** — A spanning item (= `grid-row: span N` or
  `grid-row: A / B` with `B - A > 1`) contributes to the
  intrinsic sizing of EACH spanned track per
  `GridSizing.ResolveIntrinsicTracks`. Cycle 6a's approximation:
  `perTrackContribution = itemContribution / span`. The item's
  outer contribution (= declared dimension + chrome) is divided
  equally across spanned intrinsic tracks. A spanning item with
  no intrinsic tracks in its span (= all definite-length /
  fr / minmax-definite) doesn't grow any track.
- **Missing** — Per CSS Grid L1 §11.5.1 step 3, the spec
  distributes a spanning item's contribution as follows:
  - Subtract the BaseSize contributions of any spanned tracks
    with definite (Length / Fr-with-min) base sizing.
  - The remainder is distributed across the spanning intrinsic
    tracks proportional to each track's intrinsic-size
    contribution (= proportional-to-headroom), not equal-share.
  - When the remainder is negative (= sum of definite bases
    exceeds the item's contribution), no growth is distributed
    (= the intrinsic tracks stay at their current bases).
- **Trigger** — corpus invoice / report uses `grid-row: span N`
  with mixed-kind tracks (some length / fr, some auto /
  min-content) AND the equal-share approximation produces
  visible mis-sizing (e.g., the spanning item over-grows the
  intrinsic tracks because the per-track equal share exceeds
  what the spec would distribute after subtracting definite-
  track contributions).
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` —
    `ResolveIntrinsicTracks` (cycle 6a per-track contribution
    block) extended to walk the spanned-track classification
    pre-pass + apply the spec-strict subtract-then-distribute
    algorithm.
- **Added** — Phase 3 Task 18 cycle 6a (this branch).
- **Removal condition** — `ResolveIntrinsicTracks` implements
  the §11.5.1 step 3 distribution-proportional algorithm + a
  test pins a representative mixed-kind span case (e.g.,
  `grid-template-rows: 100px auto auto` with a `grid-row: 1 /
  4` item of intrinsic 200px → spec says auto rows each get
  50, equal-share approximation gives each 200/3 ≈ 67).

---

## grid-reverse-auto-placement-deferral

- **ID** — `grid-reverse-auto-placement-deferral`
- **Status** — `approximated`. Phase 3 Task 18 cycle 6a (post-
  PR-#103 review F7).
- **Behavior** — `grid-row-start: auto; grid-row-end: <integer>`
  (and the column-axis analog) — the "auto start with definite
  end" case — currently collapses to a single cell at row
  `end - 1`. The placement-approximated diagnostic
  `LAYOUT-GRID-PLACEMENT-APPROXIMATED-001` fires so authors see
  they're in approximation territory.
- **Missing** — Per CSS Grid L1 §8.5 step 4, the spec's full
  reverse-auto-placement searches BACKWARD from the item's
  end line for the FIRST free row run of the item's span. The
  cycle-6a simplification ignores the search and just lands a
  single cell.
- **Trigger** — corpus invoice / report uses `grid-row: auto /
  N` for an item with an explicit span (e.g., `grid-row: auto /
  span 3` would be currently mishandled as auto/3 → single cell
  at row 2; the spec wants the item placed BACKWARD from row 2
  spanning 3 rows above), OR a user-reported case where
  auto-start-definite-end items render at the wrong row.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — `ReadPlacement`
    auto-start-definite-end branch (currently emits diagnostic +
    returns single-cell). Replace with a reverse-search algorithm
    that scans backward from the end line for a free run.
- **Added** — Phase 3 Task 18 cycle 6a (this branch).
- **Removal condition** — `ReadPlacement` (or the placement
  service) implements the spec's reverse-auto-placement search;
  the placement-approximated diagnostic no longer fires for the
  auto-start-definite-end case + a test pins the spec-correct
  behavior (e.g., `grid-row: auto / 4; height: 50px` in a
  4-row grid + 2 prior rows occupied → item lands at row 3
  not row 2).

---

## grid-implicit-named-area-and-occurrence-syntax-deferral

- **ID** — `grid-implicit-named-area-and-occurrence-syntax-deferral`
- **Status** — `approximated`. Phase 3 Task 18 cycle 7b (post-
  PR-#106 review). Replaces the
  `grid-named-line-placement-deferral` (= the F11 placeholder added
  in cycle 7a). Cycle 7b shipped the line-map lookup with first-
  occurrence resolution + spec-correct ordering (`<ident>-start` /
  `<ident>-end` tried before bare `<ident>`); the remaining gaps
  are itemized below.
- **Behavior** — Cycle 7b `GridSizing.ReadPlacement` resolves
  `<custom-ident>` references via a per-axis named-line occurrence
  map built from `grid-template-rows` / `grid-template-columns`
  authored lines + `grid-template-areas`-derived implicit `<area>-
  start` / `<area>-end` lines. Per CSS Grid L1 §8.3 the resolution
  order is: for a start longhand, try `<ident>-start` (first
  occurrence) → bare `<ident>` (first occurrence). For an end
  longhand: `<ident>-end` (first occurrence) → bare `<ident>`.
  Missing forms still fall back to auto-placement with
  `LAYOUT-GRID-PLACEMENT-APPROXIMATED-001`:
  - **Implicit named areas from author named-line pairs**: per
    CSS Grid L1 §8.4, a `[foo-start] … [foo-end]` pair in
    `grid-template-rows` AND `grid-template-columns` together
    creates an IMPLICIT named area `foo` even when `foo` is
    absent from `grid-template-areas`. Cycle 7b only derives
    lines from areas — not areas from line-pairs.
  - **`<integer> <custom-ident>`**: e.g., `grid-row-start: foo 2`
    = the 2nd occurrence of line named `foo`. The parser AST
    carries this via `GridLineValue.ForNamedLineNumber`; the
    placement service falls back to auto.
  - **`span <custom-ident>`**: e.g.,
    `grid-row-end: span foo` = span to the next line named
    `foo` after the start line. Parser via
    `GridLineValue.ForSpanName`; placement falls back.
  - **`span <custom-ident> <integer>`**: e.g.,
    `grid-row-end: span foo 2` = span to the 2nd line named
    `foo`. Parser via
    `GridLineValue.ForSpanNameOccurrence`; placement falls back.
- **Missing** —
  - A reverse implicit-named-area derivation pass that walks both
    axes' line maps and registers a `GridAreaRect` in
    `GridTemplateAreas.NameToRect` whenever `foo-start` AND
    `foo-end` exist on BOTH axes.
  - Occurrence-aware lookup helpers that accept a 1-based count
    (with negative-counts-from-end semantics per §8.3).
  - Span-by-name resolution that walks the occurrence list
    forward from the resolved start line.
- **Trigger** — corpus invoice / report uses
  `[foo-start] … [foo-end]` line pairs without a corresponding
  `grid-template-areas` entry, OR `grid-row-start: foo 2` / `span
  foo` occurrence syntax, OR a user-reported case where the
  authored line pair fails to produce an implicit `grid-area: foo`
  resolution.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — `BuildNamedLineMap`
    (= would build a NamedAreaMap by intersecting the two axes'
    line maps for matching `*-start` / `*-end` pairs) +
    `ReadPlacement` (= would consume occurrence counts +
    span-by-name).
- **Added** — Phase 3 Task 18 cycle 7b (post-PR-#106 review).
- **Removal condition** — the reverse implicit-named-area
  derivation ships AND `<integer> <custom-ident>` / `span <ident>`
  / `span <ident> <int>` resolve correctly AND production-pipeline
  tests pin (a) `[foo-start]…[foo-end]` line pairs producing an
  implicit `grid-area: foo` rectangle, (b) `grid-row-start: foo 2`
  resolving to the 2nd line named `foo`, and (c) `grid-row-end:
  span foo` spanning to the next `foo` line after the start.

---

## grid-auto-fit-collapse-empty-tracks-deferral

- **ID** — `grid-auto-fit-collapse-empty-tracks-deferral`
- **Status** — `approximated`. Phase 3 Task 18 cycle 7c.
- **Behavior** — Cycle 7c ships `repeat(auto-fill, …)` and
  `repeat(auto-fit, …)` expansion via the container-aware
  `ExpandTrackList` overload that derives the iteration count
  from `(containerExtent − otherFixedSizes) ÷ patternFixedSize`,
  clamped to ≥ 1 and to `MaxImplicitTracksPerAxis`. For cycle 7c
  `auto-fit` is treated IDENTICALLY to `auto-fill` — both produce
  the same expanded track list. Per CSS Grid L1 §7.2.3.1,
  `auto-fit` additionally collapses empty tracks (= tracks with
  no placed items) to 0 size and merges their surrounding
  gutters AFTER placement; that collapse pass is the missing
  piece.
- **Missing** — A post-placement pass that walks each
  auto-fit-derived track and:
  - tags tracks with no items in the placement occupancy as
    "collapsed";
  - shrinks collapsed tracks to 0 size in
    `ResolveTrackSizes`'s `TrackSizingInfo` output;
  - merges adjacent gutters around collapsed tracks per
    §7.2.3.1.
- **Trigger** — corpus invoice / report uses
  `grid-template-columns: repeat(auto-fit, minmax(100px, 1fr))`
  with FEWER items than the derived count (= empty trailing
  tracks expected to collapse for centering), OR a user-reported
  case where `auto-fit` and `auto-fill` render IDENTICALLY when
  the spec intends `auto-fit` to compress.
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/GridSizing.cs` — `Resolve`
    would track which tracks came from an `auto-fit` repeat
    (= add an `IsAutoFitDerived` flag to `TrackSizingInfo`);
    after placement, walk the occupancy + collapse empties.
  - `src/NetPdf.Layout/Layouters/GridLayouter.cs` — emission
    layer reads the post-collapse positions verbatim.
- **Added** — Phase 3 Task 18 cycle 7c (this branch).
- **Removal condition** — `auto-fit` collapses empty tracks to 0
  + gutters merge appropriately + a production-pipeline test
  pins `repeat(auto-fit, …)` rendering DIFFERENTLY from
  `repeat(auto-fill, …)` when the item count is less than the
  derived track count.

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
     the `PAINT-*-ALPHA-APPROXIMATED-001` diagnostics are retired). Also remaining:
     background images / gradients, border-radius, mitered corners. The `NetPdf.Paint`
     `DisplayCommand` IR still has no fragment→command or command→PDF consumer — the
     bridge emits straight to `IContentStream`.
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
         invalid-value diagnostic). STATUS: (a) **cross-page "running" persistence** DEFERRED — the value
         carried to later pages until re-set (needs the multi-page driver); (b) **`string-set: … content()`
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
         is near-always unwanted, so `visible` must be DECLARED to opt out). STILL deferred for `element()`: the running element's REAL
         nested BLOCK LAYOUT (sub-boxes with their OWN decoration / margins — still FLATTENED text per direct
         block child) + deep recursion (each direct block child → one line); the box/element being SEPARATELY-decorated
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
         its initial WHOLE (no half-applied axes). STILL DEFERRED: container width/padding
         affecting CHILD line geometry (sub-box wrap) + inline-level spans; margin-box
         background VARIANTS (initial only); `background-origin`/`-clip`/`-attachment` (the
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
         `!important` still beats a `:first` normal). STILL DEFERRED: `@page :left`/`:right`/`:blank`
         + named-page selectors — recognized by `ClassifyPageSelector` (→ `Deferred`) but NOT applied,
         because they need the multi-page driver's page context (which page is left/right/blank, or
         what `page:` name a page was assigned). `calc()` / font-relative margin units also deferred
         (absolute lengths + percentages are done).
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
