// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate.Diagnostics;

/// <summary>
/// Per Phase 3 Task 5 — Paginate-layer mirror of <c>NetPdf.IDiagnosticsSink</c>.
/// The integrating layouter receives <see cref="PaginateDiagnostic"/>
/// instances from the paginator; the facade adapts these to the
/// public <c>IDiagnosticsSink</c> at the assembly boundary.
///
/// <para>Same pattern as
/// <c>NetPdf.Css.Diagnostics.ICssDiagnosticsSink</c> — an
/// internal-to-the-pipeline interface that the public facade adapts.
/// Per PR #24 review pass (P0 defensive) — XML doc kept as plain
/// text rather than cross-assembly cref so the doc resolves
/// regardless of NetPdf.Paginate's transitive references.</para>
///
/// <para><b>Thread safety.</b> Implementations should be thread-safe;
/// pagination may emit diagnostics from background threads (e.g.,
/// future parallel-fragmentainer rendering). The default facade
/// adapter forwards to the public sink without additional buffering.</para>
/// </summary>
internal interface IPaginateDiagnosticsSink
{
    /// <summary>Emit a single diagnostic. Implementations MUST NOT
    /// throw; if buffering is necessary, swallow + log internally.
    ///
    /// <para>Per PR #24 review pass — emission sites in the pagination
    /// pipeline (<see cref="LayoutRetryCoordinator"/> +
    /// <see cref="OptimizingBreakResolver"/>) wrap the call in
    /// try/catch defensively. A misbehaving sink (one that violates
    /// the no-throw contract) cannot take down the layout pipeline;
    /// the exception is dropped on the floor + the layouter's retry
    /// state stays consistent. Tests with a recording sink rely on
    /// the no-throw contract; tests with a throwing sink verify the
    /// pipeline survives.</para></summary>
    void Emit(PaginateDiagnostic diagnostic);
}
