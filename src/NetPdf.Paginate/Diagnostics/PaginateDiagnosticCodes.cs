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
}
