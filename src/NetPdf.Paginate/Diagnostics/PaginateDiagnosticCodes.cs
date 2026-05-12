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
}
