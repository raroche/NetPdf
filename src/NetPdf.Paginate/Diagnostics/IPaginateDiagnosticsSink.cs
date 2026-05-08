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
/// <see cref="NetPdf.Css.Diagnostics.ICssDiagnosticsSink"/> — an
/// internal-to-the-pipeline interface that the public facade adapts.</para>
///
/// <para><b>Thread safety.</b> Implementations should be thread-safe;
/// pagination may emit diagnostics from background threads (e.g.,
/// future parallel-fragmentainer rendering). The default facade
/// adapter forwards to the public sink without additional buffering.</para>
/// </summary>
internal interface IPaginateDiagnosticsSink
{
    /// <summary>Emit a single diagnostic. Implementations MUST NOT
    /// throw; if buffering is necessary, swallow + log internally.</summary>
    void Emit(PaginateDiagnostic diagnostic);
}
