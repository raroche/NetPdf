// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;

namespace NetPdf.Diagnostics;

/// <summary>
/// Per Task 17 review Rec 4 — adapter that wraps a public
/// <see cref="IDiagnosticsSink"/> as an internal
/// <see cref="ICssDiagnosticsSink"/>, converting each <see cref="CssDiagnostic"/>
/// into the public <see cref="Diagnostic"/> shape at the boundary. The
/// public sink is the single collector for ALL conversion diagnostics
/// (HTML parsing, CSS cascade + resolvers + box building, paint stages).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an adapter, not a unified type.</b> <c>NetPdf.Css</c> cannot
/// reference the public facade (<c>NetPdf</c>) — that direction would create
/// a project-reference cycle (the facade already references
/// <c>NetPdf.Css</c>). The adapter sits at the seam in the facade assembly
/// where both types are visible.
/// </para>
/// <para>
/// <b>Severity / location mapping.</b> 1:1: <see cref="CssDiagnosticSeverity.Info"/> →
/// <see cref="DiagnosticSeverity.Info"/>, <see cref="CssDiagnosticSeverity.Warning"/> →
/// <see cref="DiagnosticSeverity.Warning"/>, <see cref="CssDiagnosticSeverity.Error"/> →
/// <see cref="DiagnosticSeverity.Error"/>. <see cref="CssSourceLocation"/>
/// (Source / Line / Column) → <see cref="SourceLocation"/> (File / Line / Column).
/// </para>
/// <para>
/// <b>Null sink no-op.</b> Constructing with <see langword="null"/>
/// produces a sink whose <see cref="Emit"/> drops the diagnostic — so
/// callers never need to null-check before forwarding. <see cref="ForOptions"/>
/// returns <see langword="null"/> when the public sink is also null, so
/// downstream stages can fast-skip the entire emission path.
/// </para>
/// </remarks>
internal sealed class PublicDiagnosticsSinkAdapter : ICssDiagnosticsSink
{
    private readonly IDiagnosticsSink _publicSink;

    public PublicDiagnosticsSinkAdapter(IDiagnosticsSink publicSink)
    {
        ArgumentNullException.ThrowIfNull(publicSink);
        _publicSink = publicSink;
    }

    /// <summary>Convenience: returns an adapter wrapping
    /// <see cref="HtmlPdfOptions.Diagnostics"/>, or <see langword="null"/>
    /// when the options carry no public sink. Callers that take
    /// <see cref="ICssDiagnosticsSink"/>? can pass the result directly.</summary>
    public static ICssDiagnosticsSink? ForOptions(HtmlPdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Diagnostics is null
            ? null
            : new PublicDiagnosticsSinkAdapter(options.Diagnostics);
    }

    public void Emit(CssDiagnostic diagnostic)
    {
        _publicSink.Emit(new Diagnostic(
            Code: diagnostic.Code,
            Message: diagnostic.Message,
            Severity: ConvertSeverity(diagnostic.Severity),
            Location: ConvertLocation(diagnostic.Location)));
    }

    private static DiagnosticSeverity ConvertSeverity(CssDiagnosticSeverity severity) => severity switch
    {
        CssDiagnosticSeverity.Info => DiagnosticSeverity.Info,
        CssDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
        CssDiagnosticSeverity.Error => DiagnosticSeverity.Error,
        _ => DiagnosticSeverity.Warning,
    };

    private static SourceLocation ConvertLocation(CssSourceLocation location) =>
        new(location.Source, location.Line, location.Column);
}
