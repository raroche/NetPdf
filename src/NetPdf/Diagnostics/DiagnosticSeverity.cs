// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Severity of a <see cref="Diagnostic"/>. <see cref="Error"/> means the input was rejected
/// when <see cref="FeatureFlags.StrictUnsupportedCss"/> is set.
/// </summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
