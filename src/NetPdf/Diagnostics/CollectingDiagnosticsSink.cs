// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace NetPdf.Diagnostics;

/// <summary>
/// The conversion-scoped diagnostics hub. Buffers every <see cref="Diagnostic"/>
/// (so <see cref="HtmlPdf.ConvertDetailed(string, HtmlPdfOptions?)"/> can return the
/// full set) while forwarding each one live to the caller's
/// <see cref="HtmlPdfOptions.Diagnostics"/> sink, if any. Every stage's diagnostics
/// — HTML / CSS (via <see cref="PublicDiagnosticsSinkAdapter"/>), layout (via
/// <see cref="PaginateToPublicDiagnosticsAdapter"/>), and paint / emit — funnel
/// through one instance so the buffer and the live stream stay in agreement.
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="IDiagnosticsSink"/> documents that NetPdf may emit from
/// background threads, and the backing list is not concurrency-safe, so writes are
/// lock-guarded. Forwarding mirrors <see cref="PublicDiagnosticsSinkAdapter"/> — a
/// caller sink that throws is the caller's contract violation and propagates.
/// </remarks>
internal sealed class CollectingDiagnosticsSink : IDiagnosticsSink
{
    private readonly IDiagnosticsSink? _forward;
    private readonly List<Diagnostic> _items = new();
    private readonly object _gate = new();

    public CollectingDiagnosticsSink(IDiagnosticsSink? forward = null) => _forward = forward;

    /// <summary>A snapshot of the diagnostics buffered so far.</summary>
    public IReadOnlyList<Diagnostic> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public void Emit(Diagnostic diagnostic)
    {
        lock (_gate) _items.Add(diagnostic);
        _forward?.Emit(diagnostic);
    }
}
