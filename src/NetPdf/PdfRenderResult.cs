// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Detailed result returned by <see cref="HtmlPdf.ConvertDetailed(string, HtmlPdfOptions?)"/>.
/// Pair with the simple <see cref="HtmlPdf.Convert(string, HtmlPdfOptions?)"/> overload when
/// you don't care about metrics; this is the dashboard / observability surface.
/// </summary>
public sealed class PdfRenderResult
{
    /// <summary>The PDF bytes.</summary>
    public required byte[] Pdf { get; init; }

    /// <summary>All non-fatal diagnostics emitted during conversion.</summary>
    public required IReadOnlyList<Diagnostic> Warnings { get; init; }

    /// <summary>Distinct unsupported features encountered (parsed but not rendered).</summary>
    public required IReadOnlyList<UnsupportedFeature> UnsupportedFeatures { get; init; }

    /// <summary>Resources referenced by HTML/CSS that failed to load.</summary>
    public required IReadOnlyList<ResourceFailure> ResourceFailures { get; init; }

    /// <summary>Layout-stage metrics.</summary>
    public required LayoutMetrics LayoutMetrics { get; init; }

    /// <summary>Per-stage wall-clock timings.</summary>
    public required TimingBreakdown Timing { get; init; }

    /// <summary>Number of pages in the produced PDF.</summary>
    public required int PageCount { get; init; }
}
