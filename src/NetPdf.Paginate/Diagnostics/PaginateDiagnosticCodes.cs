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
}
