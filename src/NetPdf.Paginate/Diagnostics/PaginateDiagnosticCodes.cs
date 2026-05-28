// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate.Diagnostics;

/// <summary>
/// Per Phase 3 Task 5 — Diagnostic-code constants emitted by the
/// pagination pipeline. The string values mirror the
/// <c>NetPdf.DiagnosticCodes</c> facade-side constants — but defined
/// here so <c>NetPdf.Paginate</c> doesn't need a back-reference to
/// the facade. Code values are stable per
/// <c>docs/diagnostics-codes.md</c>; the diagnostic-codes parity tests
/// verify the two sides agree.
///
/// <para>Same pattern as <c>NetPdf.Css.Diagnostics.CssDiagnosticCodes</c>
/// — internal-to-the-pipeline string-constant mirror of the public
/// facade's diagnostic codes. Per PR #24 review pass (P0 defensive)
/// — kept as plain text rather than cross-assembly cref.</para>
/// </summary>
internal static class PaginateDiagnosticCodes
{
    /// <summary>The bounded DP optimizer in <c>NetPdf.Paginate</c>
    /// exceeded its time / candidate-set budget for the document
    /// under layout, and the paginator fell back to greedy pagination.
    /// Mirrors <c>NetPdf.DiagnosticCodes.PaginationOptimizerFallback001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Info"/>.</summary>
    public const string PaginationOptimizerFallback001 = "PAGINATION-OPTIMIZER-FALLBACK-001";

    /// <summary>A region marked <c>break-inside: avoid</c> (or
    /// otherwise un-splittable per the cost model) is taller than a
    /// single fragmentainer (page) and had to be split anyway. Per
    /// Phase 3 plan §"Re-layout loop bound" — emitted by
    /// <c>LayoutRetryCoordinator</c> after the 2-retry budget is
    /// exhausted + a last-resort attempt commits best-effort.
    /// Mirrors <c>NetPdf.DiagnosticCodes.PaginationForcedOverflow001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string PaginationForcedOverflow001 = "PAGINATION-FORCED-OVERFLOW-001";

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 — emitted by
    /// <c>BlockLayouter</c> when an inline-only block container is
    /// encountered but the layouter was constructed without an
    /// <c>IShaperResolver</c>. The block's inline children are skipped
    /// (fragmentainer cursor + margin-collapse chain reset as if the
    /// children were unrenderable), preserving the pre-sub-cycle-1
    /// behavior. Mirrors <c>NetPdf.DiagnosticCodes.LayoutInlineSkippedNoShaperResolver001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutInlineSkippedNoShaperResolver001 = "LAYOUT-INLINE-SKIPPED-NO-SHAPER-RESOLVER-001";

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
    /// review Finding #4 — emitted by <c>BlockLayouter</c> when the
    /// inline content of an inline-only block contains an atomic
    /// inline descendant (<c>BoxKind.InlineBlockContainer</c> /
    /// <c>BoxKind.InlineFlexContainer</c> /
    /// <c>BoxKind.InlineGridContainer</c> / <c>BoxKind.InlineTable</c>
    /// / <c>BoxKind.InlineReplacedElement</c>). Sub-cycle 1 skips
    /// the atomic inline in the resulting line; sub-cycle 2 will
    /// integrate the dedicated layouter for each kind via an
    /// intrinsic-sizing seam. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutInlineAtomicNotSupported001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutInlineAtomicNotSupported001 = "LAYOUT-INLINE-ATOMIC-NOT-SUPPORTED-001";

    /// <summary>Per Phase 3 Task 11 cycle 1 sub-cycle 1 hardening
    /// review Finding #6 — emitted by <c>BlockLayouter</c> when
    /// <c>InlineLayouter.LayoutPerRun</c> throws
    /// <see cref="System.NotSupportedException"/> for a configuration
    /// the inline layouter doesn't yet support (e.g., per-source-
    /// TextRun <c>word-break: keep-all</c> mismatch — CJK semantics
    /// need UAX #24 script detection). The inline-only block emits
    /// no fragment + the block layouter advances past it (same
    /// chain-reset semantics as the no-resolver skip). Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutInlineUnsupported001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutInlineUnsupported001 = "LAYOUT-INLINE-UNSUPPORTED-001";

    /// <summary>Per Phase 3 Task 12 sub-cycle 1 — emitted by
    /// <c>TableLayouter</c> when the input box tree exercises a CSS
    /// Tables L3 feature the sub-cycle 1 algorithm doesn't yet
    /// implement (currently: <c>colspan</c> / <c>rowspan</c> cell
    /// merging; sub-cycle 2 will add table-layout: auto / fixed,
    /// border-collapse, captions, &lt;col&gt; widths, header/footer
    /// repetition across pages). The table renders with the feature
    /// silently ignored. See
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c> for
    /// the full deferral list. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutTableFeatureUnsupported001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableFeatureUnsupported001 = "LAYOUT-TABLE-FEATURE-UNSUPPORTED-001";

    /// <summary>Per Phase 3 Task 12 sub-cycle 2 hardening Finding 4 —
    /// emitted by <c>TableLayouter</c> when a table's cumulative
    /// <c>rowspan × colspan</c> slot count would exceed the
    /// <c>MaxOccupiedSlots</c> DoS guard (1M slots). Cells crossing
    /// the budget are capped at <c>rowspan = colspan = 1</c>; the
    /// table still renders (truncated geometry, not dropped content)
    /// but the author's recorded spans were ignored. Defends against
    /// hostile HTML where legal attribute values (e.g.,
    /// <c>rowspan="65534" colspan="1000"</c>) could force unbounded
    /// CPU + memory work in the placement pass. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutTableSlotBudgetExceeded001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableSlotBudgetExceeded001 = "LAYOUT-TABLE-SLOT-BUDGET-EXCEEDED-001";

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 hardening Finding 1 —
    /// emitted by <c>TableLayouter</c> when the sum of declared
    /// column widths under <c>table-layout: fixed</c> exceeds the
    /// table wrapper's content-inline-size. CSS 2.1 §17.5.2.1 says the
    /// table grid's inline extent grows to fit the declared column
    /// widths in that case — the table overflows its wrapper in the
    /// inline axis. The layouter keeps the declared widths intact
    /// (row + caption fragments grow to the column sum); the
    /// diagnostic surfaces the overflow so authors can tune their
    /// declarations. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutTableInlineOverflow001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableInlineOverflow001 = "LAYOUT-TABLE-INLINE-OVERFLOW-001";

    /// <summary>Per Phase 3 Task 12 sub-cycle 5 hardening Finding 4 —
    /// emitted by <c>TableLayouter</c> when the auto-table-layout
    /// intrinsic-measurement pass exceeds its per-table speculative-
    /// measurement budget. Each cell normally runs two speculative
    /// nested <c>BlockLayouter</c> passes (min-content at 1px +
    /// max-content at 1e6 px) — i.e., 2 ops per cell. For tables with
    /// thousands of cells the cumulative work is bounded by this budget;
    /// cells beyond the cap fall back to <c>(minContent=0,
    /// maxContent=contentInlineSize)</c>, producing a degenerate
    /// equal-split-like distribution rather than DoS-amplifying the
    /// speculative passes. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutTableIntrinsicMeasurementBudgetExceeded001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableIntrinsicMeasurementBudgetExceeded001 =
        "LAYOUT-TABLE-INTRINSIC-MEASUREMENT-BUDGET-EXCEEDED-001";

    /// <summary>Per Phase 3 Task 13 cycle 1 hardening Finding 5 —
    /// emitted by <c>TableLayouter</c> when the break resolver returns
    /// <see cref="BreakAction.Rewind"/> at a table row boundary. Cycle
    /// 1 does NOT register per-row checkpoints (the outer
    /// <c>BlockLayouter</c> owns the pre-table rewind frontier), so the
    /// resolver naming a checkpoint the table never registered is a
    /// contract violation. The layouter falls back to
    /// <see cref="BreakAction.Continue"/> (preserving the pre-finding
    /// behavior) + surfaces this diagnostic so authors / integrators
    /// see the dropped rewind. Per-row checkpoint capture is cycle 2+
    /// scope. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutTableRewindNotSupported001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableRewindNotSupported001 =
        "LAYOUT-TABLE-REWIND-NOT-SUPPORTED-001";

    /// <summary>Per Phase 3 Task 13 cycle 1 hardening Finding 6 —
    /// emitted by <c>TableLayouter</c> when a row break would cut
    /// through a cell whose <c>rowspan&gt;1</c> origin row commits on
    /// the current page but whose span extends past the break. Cycle
    /// 1 keeps rowspan cells atomic across pages: the layouter forces
    /// the break BEFORE the rowspan origin row (the whole spanning
    /// cell stays together on the next page) when at least one row +
    /// optional captions have already committed on the current page;
    /// otherwise it falls back to the existing forced-overflow path.
    /// CSS Tables L3 §11 spec-strict rowspan distribution across
    /// pages is sub-cycle 6+ scope. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutTableRowspanCrossesPage001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableRowspanCrossesPage001 =
        "LAYOUT-TABLE-ROWSPAN-CROSSES-PAGE-001";

    /// <summary>Per Phase 3 Task 13 cycle 2 — emitted by
    /// <c>TableLayouter</c> when the combined <c>&lt;thead&gt;</c> +
    /// <c>&lt;tfoot&gt;</c> stack height (header rows + footer rows)
    /// exceeds the fragmentainer's available block-size, leaving no
    /// room to repeat the header + footer on every page along with
    /// any body row. Per CSS Tables L3 §3.6 / §11 the header + footer
    /// repeat at the top + bottom of each page; if they exceed the
    /// fragmentainer, no body row can fit on a page that ALSO honors
    /// the repeat contract. The layouter commits the header + footer
    /// once (atomically) on the current page, skips the body to avoid
    /// infinite continuation loops, and surfaces this diagnostic so
    /// authors can reduce header / footer content or widen the page.
    /// Mirrors <c>NetPdf.DiagnosticCodes.LayoutTableHeaderFooterOversized001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutTableHeaderFooterOversized001 =
        "LAYOUT-TABLE-HEADER-FOOTER-OVERSIZED-001";

    /// <summary>Per Phase 3 Task 14 cycle 2 hardening (Finding #3) —
    /// emitted by <c>MulticolLayouter</c> when the author-supplied
    /// <c>column-count</c> exceeds the layouter's
    /// <c>MaxColumnCount</c> safety cap (= 1000) and is silently
    /// clamped. Without surfacing this, a stray <c>column-count:
    /// 100000</c> would produce N=1000 columns + the rendered output
    /// would visually disagree with the stylesheet — but emit no
    /// warning, hiding the cap as a silent DoS-mitigation behavior.
    /// The clamp is intentional (the layouter's per-column
    /// arithmetic is O(N) per child; uncapped N is a DoS vector for
    /// adversarial input), but authors who hit the cap legitimately
    /// (e.g., generated CSS) need to know that the requested column
    /// count was reduced. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutMulticolColumnCountClamped001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutMulticolColumnCountClamped001 =
        "LAYOUT-MULTICOL-COLUMN-COUNT-CLAMPED-001";

    /// <summary>Per Phase 3 Task 14 cycles 1-2 — emitted by
    /// <c>MulticolLayouter</c> /<c>BlockLayouter</c> when a multicol
    /// container's in-flow content can't make forward progress
    /// through the N columns + the available page space. Cycle 2's
    /// multi-page multicol ships clean page splitting (via
    /// <c>MulticolContinuation</c>); this diagnostic NARROWED its
    /// semantics in cycle 2 — a clean multi-page split is no longer
    /// an error.
    ///
    /// <para><b>Cycle 2 — when this fires:</b></para>
    /// <list type="bullet">
    ///   <item><b>No-forward-progress (MulticolLayouter):</b> a
    ///   resume page entered with a <c>MulticolContinuation</c>
    ///   re-runs the column-fill pass + emits ZERO fragments +
    ///   returns a continuation that doesn't advance past the entry
    ///   index. The layouter truncates the remainder + emits the
    ///   diagnostic to surface the loss. Analog to TableLayouter's
    ///   single-oversized-row case.</item>
    /// </list>
    ///
    /// <para>Per post-PR-#57 review #2 Finding #2 — the deep-nested
    /// branch of this diagnostic (multicol at recursion depth ≥ 2
    /// inside <c>EmitBlockSubtreeRecursive</c>) was REMOVED in the
    /// cycle 2 hardening (Finding #1)'s multi-level continuation
    /// propagation lift. The recursion now returns a chained
    /// <c>LayoutContinuation</c> that flows up through any DOM depth,
    /// so deep-nested multicols split cleanly. This diagnostic now
    /// fires only for the no-forward-progress fallback case described
    /// above.</para>
    ///
    /// <para>Pre-cycle-2 the diagnostic fired for ANY overflow past
    /// the N columns; cycle 1 always truncated. Cycle 2 ships the
    /// clean multi-page split that suppresses the diagnostic in the
    /// common case. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutMulticolForcedOverflow001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</para></summary>
    public const string LayoutMulticolForcedOverflow001 =
        "LAYOUT-MULTICOL-FORCED-OVERFLOW-001";

    /// <summary>Per Phase 3 Task 14 cycle 1 hardening (Finding 4) —
    /// emitted by <c>MulticolLayouter</c> when an arithmetic
    /// combination of <c>column-count</c> + <c>column-gap</c> would
    /// produce non-finite per-column inline-axis geometry (e.g.,
    /// <c>column-gap: 1e300</c> with 100 columns drives
    /// <c>totalGap = (N-1) * columnGap</c> past <c>double.MaxValue</c>
    /// → <c>Infinity</c>, which then propagates through the per-column
    /// offset arithmetic). The CSS resolver's NaN / ±Infinity gate
    /// catches most pathological inputs at parse time; this code
    /// defends against the multiplicative blow-up that can still arise
    /// from individually-finite operands. The layouter clamps the bad
    /// value to a sane cap (column-gap is forced to a value that keeps
    /// <c>totalGap &lt; containerInlineSize / 2</c>) so emission can
    /// continue; the rendered geometry differs visually from author
    /// intent but doesn't NaN-poison downstream pagination math.
    /// Mirrors <c>NetPdf.DiagnosticCodes.LayoutMulticolNonFiniteGeometry001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutMulticolNonFiniteGeometry001 =
        "LAYOUT-MULTICOL-NON-FINITE-GEOMETRY-001";

    /// <summary>Per Phase 3 Task 14 cycle 2 hardening (Finding #1) —
    /// emitted by <c>BlockLayouter</c> when a float subtree's nested
    /// recursion returns a non-null <c>LayoutContinuation</c>
    /// (indicating a multicol or table inside the float broke mid-
    /// emission). Floats are out-of-flow per CSS 2.2 §9.5; propagating
    /// their continuation through the in-flow pagination machinery
    /// requires float-tracking machinery that's an existing Phase 3
    /// Task 8 deferral (cycle 3+ scope). The layouter discards the
    /// returned continuation (atomic-fallback behavior) + surfaces
    /// this diagnostic so authors / integrators see the truncation
    /// rather than wondering why content disappeared from the float.
    /// The diagnostic fires at most once per page to avoid spam from
    /// pages with many such floats. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutFloatBreakInsideNested001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutFloatBreakInsideNested001 =
        "LAYOUT-FLOAT-BREAK-INSIDE-NESTED-001";

    /// <summary>Per Phase 3 Task 15 L6 post-PR-#66 review F#4 —
    /// emitted by <c>FlexLayouter</c> when the flex container's
    /// <c>flex-wrap</c> property resolves to <c>wrap-reverse</c>. L6
    /// ships <c>wrap</c> in full (multi-line greedy packing, per-line
    /// alignment, sum-of-lines auto cross-size); <c>wrap-reverse</c>
    /// requires an additional cross-axis line-stacking reversal
    /// transform per CSS Flexbox L1 §6.3 ("same as wrap but the
    /// cross-start and cross-end directions are swapped") which is
    /// L7+ scope. Until then the layouter approximates
    /// <c>wrap-reverse</c> as <c>wrap</c>: items still wrap correctly
    /// in main-axis DOM order, but the lines stack in the natural
    /// cross-axis direction rather than the reversed direction the
    /// author requested. Without this diagnostic the wrong rendering
    /// would be silent — the CSS declaration parses successfully but
    /// behaves like <c>flex-wrap: wrap</c>. Fires at most once per
    /// <c>AttemptLayout</c> invocation to avoid spam on per-item
    /// dispatch. Mirrors
    /// <c>NetPdf.DiagnosticCodes.LayoutFlexWrapReverseApproximated001</c>.
    /// Severity: <see cref="PaginateDiagnosticSeverity.Warning"/>.</summary>
    public const string LayoutFlexWrapReverseApproximated001 =
        "LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001";

    /// <summary>Per Phase 3 Task 17 cycle 1 (Hello World) — emitted when
    /// the <c>GridLayouter</c> encounters a track-list entry that cycle 1
    /// doesn't yet support (= anything other than <c>&lt;length&gt;</c>
    /// — fr / auto / min-content / max-content / minmax / fit-content /
    /// repeat). The cycle-0a/0b AST contract parses these forms; cycle 1
    /// only layouts pixel tracks. Future cycles add the missing track
    /// types: cycle 2 ships <c>fr</c>, cycle 3 ships intrinsic
    /// (<c>auto</c> / <c>min-content</c> / <c>max-content</c>), cycle 4
    /// ships <c>minmax()</c> / <c>fit-content()</c> / <c>repeat(int)</c>,
    /// cycle 7 ships <c>repeat(auto-fill)</c> / <c>repeat(auto-fit)</c>.
    /// Until then, non-length tracks contribute 0 px to the track sum +
    /// this diagnostic surfaces the silent drop. Fires once per
    /// AttemptLayout. Severity: Warning.</summary>
    public const string LayoutGridTrackKindUnsupported001 =
        "LAYOUT-GRID-TRACK-KIND-UNSUPPORTED-001";

    /// <summary>Per Phase 3 Task 17 cycle 1 — emitted when a grid item's
    /// declared placement (<c>grid-row-start</c> / <c>grid-column-start</c>)
    /// uses a value cycle 1 doesn't yet support: <c>span N</c> /
    /// <c>&lt;custom-ident&gt;</c> (= named line) /
    /// <c>&lt;custom-ident&gt; N</c>. The cycle-0b parser produces these
    /// forms but the cycle-1 placement algorithm treats them as
    /// <c>auto</c> (= sparse auto-placement per §8.5). Cycle 6 adds
    /// span; cycle 7 adds named lines + areas. Fires once per item.
    /// Severity: Warning.</summary>
    public const string LayoutGridPlacementApproximated001 =
        "LAYOUT-GRID-PLACEMENT-APPROXIMATED-001";

    /// <summary>Per Phase 3 Task 17 cycle 1 — emitted when an item is
    /// placed at a cell OUTSIDE the explicit grid (= row/column index
    /// exceeds the declared track count, OR a 0-track grid has no cells
    /// to place into). Per CSS Grid §7.5 the implicit grid should
    /// auto-generate tracks via <c>grid-auto-rows</c> /
    /// <c>grid-auto-columns</c>; cycle 1 doesn't yet support implicit
    /// tracks, so the item silently drops (no fragment emitted). Cycle 6
    /// adds implicit-track generation. Fires once per item. Severity:
    /// Warning.</summary>
    public const string LayoutGridImplicitTrackUnsupported001 =
        "LAYOUT-GRID-IMPLICIT-TRACK-UNSUPPORTED-001";

    /// <summary>Per Phase 3 Task 17 cycle 1 post-PR-#92 review F9 —
    /// emitted when a grid container's resolved track positions or
    /// item fragment geometry produces a non-finite value
    /// (NaN / ±Infinity). Individual track sizes are validated finite
    /// at AST construction time (cycle-0a P3 #8 factories), but
    /// cumulative sums can still overflow when summing very large
    /// finite tracks (= hostile CSS like
    /// <c>grid-template-rows: 1e300px 1e300px</c>). The layouter
    /// detects the non-finite cumulative position + emits a fragment
    /// at clamped zero-size geometry so paint / PDF emission can't
    /// corrupt downstream. Mirrors
    /// <see cref="LayoutMulticolNonFiniteGeometry001"/>. Fires once per
    /// dispatch. Severity: Warning.</summary>
    public const string LayoutGridNonFiniteGeometry001 =
        "LAYOUT-GRID-NON-FINITE-GEOMETRY-001";

    /// <summary>Per Phase 3 Task 17 cycle 2 post-PR-#93 review F2 —
    /// emitted when a grid item's cell resolves to a zero-sized area
    /// (inline-size = 0 OR block-size = 0) AND the item has child
    /// content. The item's outer fragment still emits at the cell's
    /// (zero-sized) geometry, but cycle 2's sub-BlockLayouter dispatch
    /// can't run with a non-positive content extent (=
    /// FragmentainerContext's positive-size validation). The inner
    /// content is therefore skipped + this diagnostic surfaces the
    /// silent drop. A zero-area grid cell is NOT equivalent to
    /// <c>display: none</c> per CSS — content should overflow or be
    /// clipped per the painter's overflow rules; cycle 3 will ship
    /// the zero-area inner-layout strategy. Fires per item. Severity:
    /// Warning.</summary>
    public const string LayoutGridZeroSizedCellContentSkipped001 =
        "LAYOUT-GRID-ZERO-SIZED-CELL-CONTENT-SKIPPED-001";

    /// <summary>Per Phase 3 Task 17 cycle 2 post-PR-#93 review F3 —
    /// emitted when a grid container with auto block-size (or auto
    /// inline-size for column-flow grids) contains <c>fr</c> tracks
    /// on the same axis. Per CSS Grid §11.7, flexible tracks under
    /// indefinite available space resolve via the intrinsic /
    /// max-content branch (= cycle 3 scope; needs content-derived
    /// base sizes). Cycle 2's pre-measure can't fold fr contributions
    /// into the wrapper's natural extent without that intrinsic
    /// branch, so fr tracks on the indefinite axis collapse to 0 +
    /// this diagnostic surfaces the approximation. The author's
    /// intent (= "fr fills remaining space") doesn't apply when the
    /// space is itself indefinite; either declare an explicit
    /// height/width OR wait for cycle 3 to ship intrinsic resolution.
    /// Fires once per AttemptLayout. Severity: Warning.</summary>
    public const string LayoutGridFrUnderIndefiniteApproximated001 =
        "LAYOUT-GRID-FR-UNDER-INDEFINITE-APPROXIMATED-001";

    /// <summary>Phase 3 Task 17 cycle 4 + post-PR-#95 review hardening
    /// (C3 + H3) — a grid track uses a percentage value (top-level
    /// <c>&lt;percentage&gt;</c> OR inside a <c>minmax()</c> /
    /// <c>fit-content()</c> sub-arg) that the cycle-4 sizing path
    /// doesn't yet resolve. Percentages are silently treated as 0
    /// to prevent silent pixel-vs-percent mismatch. Cycle 5+ ships
    /// percentage resolution against the container's definite
    /// extent. Fires once per AttemptLayout. Severity: Warning.</summary>
    public const string LayoutGridPercentageTrackApproximated001 =
        "LAYOUT-GRID-PERCENTAGE-TRACK-APPROXIMATED-001";

    /// <summary>Phase 3 Task 17 cycle 4 + post-PR-#95 review hardening
    /// (R2 + T4) — a grid track list's <c>repeat(N, ...)</c> expansion
    /// would exceed <c>TrackList.MaxExpandedTrackCount</c> (50,000).
    /// Expansion truncates at the cap to prevent unbounded memory
    /// allocation from hostile CSS like
    /// <c>repeat(10000, 1px 1fr 1px 1fr 1fr 1px)</c>. Items in the
    /// truncated tail aren't visible. Fires once per AttemptLayout.
    /// Severity: Warning.</summary>
    public const string LayoutGridMaxExpandedTracksTruncated001 =
        "LAYOUT-GRID-MAX-EXPANDED-TRACKS-TRUNCATED-001";

    /// <summary>Phase 3 Task 18 cycle 7c + post-PR-#107 review F2 #4 —
    /// a <c>grid-template-rows</c> / <c>-columns</c> declaration uses
    /// <c>repeat(auto-fit, …)</c>. Cycle 7c expands auto-fit
    /// IDENTICALLY to auto-fill (= the iteration count comes from
    /// <c>(containerExtent − otherFixedSizes) ÷ patternFixedSize</c>);
    /// per CSS Grid L1 §7.2.3.1, auto-fit additionally collapses
    /// tracks with no placed items to 0 size AFTER placement. That
    /// post-placement collapse is tracked under
    /// `grid-auto-fit-collapse-empty-tracks-deferral`. Fires once per
    /// <c>AttemptLayout</c>. Severity: Warning.</summary>
    public const string LayoutGridAutoFitApproximated001 =
        "LAYOUT-GRID-AUTO-FIT-APPROXIMATED-001";

    /// <summary>Phase 3 Task 17 cycle 5 — a single grid row exceeds
    /// the fragmentainer's block-axis budget on its first attempt.
    /// Per CSS Fragmentation L3 §4.4 progress rule the row is
    /// force-emitted (= "you must commit at least one element per
    /// page" or pagination would deadlock); content overflows the
    /// fragmentainer-block-end region. Cycle 5 ships row-atomic
    /// pagination only; intra-row item splitting is post-v1.
    /// Severity: Warning.</summary>
    public const string LayoutGridForcedOverflow001 =
        "LAYOUT-GRID-FORCED-OVERFLOW-001";

    /// <summary>Phase 3 Task 17 cycle 5 + post-PR-#96 review F3 — a
    /// grid resume continuation arrives at a page with a different
    /// <c>contentInlineSize</c> than the cache was built for (e.g.,
    /// left/right pages with different margins, or nested
    /// fragmentainers). Inline track sizes (fr / Maximize'd) are
    /// stale at the new size; the cache is invalidated + a fresh §11
    /// sizing + §8.5 placement pass runs. Note that sparse auto-
    /// placement is order-sensitive — a different placement may
    /// emerge from the fresh resolve if items were partially emitted
    /// on the prior page. Callers should avoid resuming a grid into
    /// a page with a different inline content size. Severity: Warning.</summary>
    public const string LayoutGridResumeInlineSizeMismatch001 =
        "LAYOUT-GRID-RESUME-INLINE-SIZE-MISMATCH-001";

    /// <summary>Phase 3 Task 17 cycle 5 + post-PR-#96 review F5 — a
    /// grid resume cache was rejected by the receiving GridLayouter
    /// because of a structural anomaly: cache GridIdentity does not
    /// match the rootBox (= cache routed to the wrong grid), inconsistent
    /// array lengths, out-of-bounds item placement, non-finite
    /// geometry, or a non-Box item payload. The cache is rejected +
    /// a fresh resolve runs. This indicates a layouter-dispatch bug
    /// in the BlockLayouter continuation routing. Severity: Warning.</summary>
    public const string LayoutGridResumeCacheRejected001 =
        "LAYOUT-GRID-RESUME-CACHE-REJECTED-001";
}
