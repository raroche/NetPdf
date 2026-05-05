// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser;

namespace NetPdf.Css.Diagnostics;

/// <summary>
/// CSS-layer mirror of <c>NetPdf.Diagnostic</c>. Defined here so <c>NetPdf.Css</c> can
/// emit diagnostics without taking a project reference back to the facade — that direction
/// would create a circular dependency (the facade already references <c>NetPdf.Css</c>).
/// The facade's <c>HtmlPdf</c> entry point converts each <see cref="CssDiagnostic"/> to its
/// public <c>Diagnostic</c> shape at the boundary.
/// </summary>
/// <param name="Code">Stable diagnostic code from <c>docs/diagnostics-codes.md</c>.</param>
/// <param name="Message">Human-readable description; may include the offending input.</param>
/// <param name="Severity">Severity classification.</param>
/// <param name="Location">Source position when known; <see cref="CssSourceLocation.Unknown"/>
/// otherwise.</param>
internal readonly record struct CssDiagnostic(
    string Code,
    string Message,
    CssDiagnosticSeverity Severity,
    CssSourceLocation Location);

/// <summary>Severity for a <see cref="CssDiagnostic"/>. 1:1 with the public
/// <c>NetPdf.DiagnosticSeverity</c> enum.</summary>
internal enum CssDiagnosticSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Sink for diagnostics emitted by the CSS pipeline. The facade implements
/// this with a thin adapter that forwards to the public <c>IDiagnosticsSink</c>
/// configured on <c>HtmlPdfOptions</c>.</summary>
internal interface ICssDiagnosticsSink
{
    void Emit(CssDiagnostic diagnostic);
}
