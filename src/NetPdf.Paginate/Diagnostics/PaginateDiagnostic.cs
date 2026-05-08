// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paginate.Diagnostics;

/// <summary>
/// Per Phase 3 Task 5 — Paginate-layer mirror of <c>NetPdf.Diagnostic</c>.
/// Defined here so <c>NetPdf.Paginate</c> can emit diagnostics without
/// taking a project reference back to the facade — that direction
/// would create a circular dependency (the facade references
/// <c>NetPdf.Paginate</c>, not the reverse). The facade's
/// <c>HtmlPdf</c> entry point converts each <see cref="PaginateDiagnostic"/>
/// to its public <c>Diagnostic</c> shape at the boundary.
///
/// <para>Same pattern as <c>NetPdf.Css.Diagnostics.CssDiagnostic</c>
/// — internal-to-the-pipeline diagnostic types that the facade adapts
/// at the boundary. Keeps <c>NetPdf.Paginate</c>'s dependency direction
/// clean.</para>
/// </summary>
/// <param name="Code">Stable diagnostic code from
/// <see cref="PaginateDiagnosticCodes"/>; mirrors the public
/// <c>NetPdf.DiagnosticCodes.PAGINATION-*</c> constants. The
/// diagnostic-codes parity tests verify the two sides agree.</param>
/// <param name="Message">Human-readable description; may include the
/// offending input (page index, attempt count, etc.). Sanitized at
/// the facade boundary before reaching the public sink.</param>
/// <param name="Severity">Severity classification.</param>
internal readonly record struct PaginateDiagnostic(
    string Code,
    string Message,
    PaginateDiagnosticSeverity Severity);

/// <summary>Severity for a <see cref="PaginateDiagnostic"/>. 1:1 with
/// the public <c>NetPdf.DiagnosticSeverity</c> enum.</summary>
internal enum PaginateDiagnosticSeverity : byte
{
    /// <summary>Informational — the renderer adapted but the user
    /// should know.</summary>
    Info = 0,

    /// <summary>Warning — the output is likely correct but a
    /// constraint was relaxed (e.g., <c>break-inside: avoid</c>
    /// honored on best-effort basis).</summary>
    Warning = 1,

    /// <summary>Error — the input was rejected; the rendered output
    /// is missing or incomplete content. Reserved for future use;
    /// pagination v1 only emits <see cref="Info"/> + <see cref="Warning"/>.</summary>
    Error = 2,
}
