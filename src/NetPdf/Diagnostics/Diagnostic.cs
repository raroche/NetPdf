// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// A diagnostic emitted during conversion — an unsupported feature, a malformed rule,
/// a failed resource load, or a raster-fallback notification. Codes are stable and
/// versioned; see <c>docs/diagnostics-codes.md</c> for the registry.
/// </summary>
public readonly record struct Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    SourceLocation Location)
{
    /// <summary>Convenience constructor with unknown source location.</summary>
    public Diagnostic(string code, string message, DiagnosticSeverity severity)
        : this(code, message, severity, SourceLocation.Unknown) { }
}
