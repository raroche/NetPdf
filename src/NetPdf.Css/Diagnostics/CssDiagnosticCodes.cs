// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Diagnostics;

/// <summary>
/// Diagnostic-code constants emitted by the CSS pipeline. The string values mirror the
/// <c>NetPdf.DiagnosticCodes</c> facade-side constants — but defined here so
/// <c>NetPdf.Css</c> doesn't need a back-reference to the facade. Code values are
/// stable per <c>docs/diagnostics-codes.md</c>; the diagnostic-codes parity tests
/// verify the two sides agree.
/// </summary>
internal static class CssDiagnosticCodes
{
    /// <summary>A CSS rule was malformed and skipped. Severity: Warning.</summary>
    public const string CssParseWarning001 = "CSS-PARSE-WARNING-001";

    /// <summary>A <c>:has()</c> selector was encountered. NetPdf does not evaluate
    /// <c>:has()</c> in v1 — the rule has no effect. Severity: Warning.</summary>
    public const string CssHasRenderingNotImplemented001 = "CSS-HAS-RENDERING-NOT-IMPLEMENTED-001";
}
