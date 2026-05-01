// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Receives diagnostics during conversion. Implementations should be thread-safe; NetPdf
/// emits diagnostics from background paint threads. Use this for streaming logs;
/// <see cref="HtmlPdf.ConvertDetailed"/> aggregates them into <see cref="PdfRenderResult"/>
/// at the end.
/// </summary>
public interface IDiagnosticsSink
{
    void Emit(Diagnostic diagnostic);
}
