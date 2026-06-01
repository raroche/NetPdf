// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Paginate.Diagnostics;

namespace NetPdf.Diagnostics;

/// <summary>
/// Adapts the layout / pagination layer's internal
/// <see cref="IPaginateDiagnosticsSink"/> to the public
/// <see cref="IDiagnosticsSink"/> at the facade boundary — the seam
/// <see cref="PaginateDiagnostic"/> documents ("the facade's HtmlPdf entry point
/// converts each PaginateDiagnostic to its public Diagnostic shape"). The
/// counterpart to <see cref="PublicDiagnosticsSinkAdapter"/> for the CSS layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Codes pass through.</b> <see cref="PaginateDiagnostic.Code"/> already holds
/// the public code string (the <c>PaginateDiagnosticCodes</c> constants mirror the
/// public <c>DiagnosticCodes</c> ones — the diagnostic-codes parity tests pin the
/// two sides together), so no mapping table is needed; only the severity enum is
/// projected. Pagination carries no source location, so the public location is
/// <see cref="SourceLocation.Unknown"/>.
/// </para>
/// <para>
/// <b>No-throw contract.</b> <see cref="IPaginateDiagnosticsSink.Emit"/> must not
/// throw (the layout pipeline relies on it), so a caller sink that throws is
/// swallowed here rather than unwinding the layout pass.
/// </para>
/// </remarks>
internal sealed class PaginateToPublicDiagnosticsAdapter : IPaginateDiagnosticsSink
{
    private readonly IDiagnosticsSink _publicSink;

    public PaginateToPublicDiagnosticsAdapter(IDiagnosticsSink publicSink)
    {
        ArgumentNullException.ThrowIfNull(publicSink);
        _publicSink = publicSink;
    }

    /// <summary>Returns an adapter wrapping <paramref name="sink"/>, or
    /// <see langword="null"/> when there's no sink — letting the layouter
    /// fast-skip the entire emission path.</summary>
    public static IPaginateDiagnosticsSink? ForSink(IDiagnosticsSink? sink) =>
        sink is null ? null : new PaginateToPublicDiagnosticsAdapter(sink);

    public void Emit(PaginateDiagnostic diagnostic)
    {
        try
        {
            _publicSink.Emit(new Diagnostic(
                diagnostic.Code,
                diagnostic.Message,
                MapSeverity(diagnostic.Severity)));
        }
        catch (Exception)
        {
            // Honor IPaginateDiagnosticsSink's no-throw contract: a misbehaving
            // caller sink must not take down the layout pipeline.
        }
    }

    private static DiagnosticSeverity MapSeverity(PaginateDiagnosticSeverity severity) => severity switch
    {
        PaginateDiagnosticSeverity.Info => DiagnosticSeverity.Info,
        PaginateDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
        PaginateDiagnosticSeverity.Error => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Warning,
    };
}
