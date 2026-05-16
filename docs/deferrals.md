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
  children as a single horizontal row. Each item emits at its natural
  inline-size (= the item's declared `width` if set, else 0) and
  natural block-size (= declared `height` if set, else 0). Items pack
  from the container's `contentInlineOffset` left-to-right; the main-
  axis cursor advances by each item's inline-size with no wrapping
  (overflow is permitted per CSS Flexbox L1 §6 + §9.4 for
  `flex-wrap: nowrap`). All items emit at `contentBlockOffset` on
  the cross-axis (= `flex-start` equivalent regardless of the
  computed `align-items` value). The flex container is atomic to
  outer pagination (the entire container's items emit on the page
  the wrapper landed on; no `FlexContinuation` resume).
- **Missing** —
  - `flex-direction: column` / `row-reverse` / `column-reverse`
  - `flex-wrap: wrap` / `wrap-reverse`
  - `justify-content` values beyond `flex-start`
  - `align-items` values beyond default `flex-start` equivalent
    (no real `stretch`, no `center` / `end` / `baseline`)
  - `flex-grow` / `flex-shrink` / `flex-basis` resolution
  - `order` property
  - Anonymous flex-item wrapping for inline-level / text children
    (cycle 1 skips non-block-level children silently; whitespace
    `TextRun`s between flex item elements are dropped without a
    fragment)
  - Multi-page flex container splitting (atomic to outer pagination
    in L1; `FlexContinuation` exists in
    `src/NetPdf.Paginate/LayoutContinuation.cs` for sub-cycle 2+)
  - Baseline alignment
- **Owner files** —
  - `src/NetPdf.Layout/Layouters/FlexLayouter.cs` — the layouter
    itself.
  - `src/NetPdf.Layout/Layouters/BlockLayouter.cs` — the `IsFlexContainer`
    predicate + the outer-loop + recursion-site dispatch into
    `FlexLayouter`.
  - `src/NetPdf.Layout/Boxes/DisplayMapper.cs` — `display: flex`
    `/ inline-flex` → `BoxKind.FlexContainer` /
    `BoxKind.InlineFlexContainer`.
  - `src/NetPdf.Css/properties.json` — the `align-items`,
    `flex-direction`, `flex-wrap`, `justify-content` keyword
    properties (already parsed; not yet honored by the layouter).
- **Trigger** — sub-cycle 2 picks up flex-direction: column +
  flex-wrap + real align-items; sub-cycle 3 picks up flex-grow /
  shrink / basis. Sub-cycle 4 picks up the anonymous-flex-item
  wrapping + the `FlexContinuation`-based multi-page split.
- **Added** — Phase 3 Task 15 cycle 1 (Hello World).
- **Removal condition** — Phase 3 Task 15 L2 + L3 ship the deferred
  features (column / wrap / justify / align / grow / shrink / basis /
  order / baseline / multi-page split / anonymous flex item).

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
