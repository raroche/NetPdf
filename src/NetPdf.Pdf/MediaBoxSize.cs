// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf;

/// <summary>
/// PDF page dimensions in PDF user-space units (points; 1 inch = 72 points). Used as the
/// width × height of <c>/MediaBox</c> when a page is added to a <see cref="PdfDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the public <c>NetPdf.PageSize</c>, which measures in CSS pixels —
/// NetPdf's canonical layout unit per the project's plan. The public facade converts
/// CSS px → PDF pt (× 0.75) at the emit boundary; this type is what the
/// <see cref="PdfDocument"/> layer consumes after that conversion. Constants below
/// match the standard sizes Acrobat / Word / Pages emit.
/// </para>
/// </remarks>
internal readonly record struct MediaBoxSize(double WidthPts, double HeightPts)
{
    /// <summary>US Letter — 8.5 × 11 in (612 × 792 pts).</summary>
    public static readonly MediaBoxSize Letter = new(612.0, 792.0);

    /// <summary>US Legal — 8.5 × 14 in (612 × 1008 pts).</summary>
    public static readonly MediaBoxSize Legal = new(612.0, 1008.0);

    /// <summary>US Tabloid — 11 × 17 in (792 × 1224 pts).</summary>
    public static readonly MediaBoxSize Tabloid = new(792.0, 1224.0);

    /// <summary>ISO A3 — 297 × 420 mm (842 × 1191 pts, rounded).</summary>
    public static readonly MediaBoxSize A3 = new(842.0, 1191.0);

    /// <summary>ISO A4 — 210 × 297 mm (595 × 842 pts, rounded).</summary>
    public static readonly MediaBoxSize A4 = new(595.0, 842.0);

    /// <summary>ISO A5 — 148 × 210 mm (420 × 595 pts, rounded).</summary>
    public static readonly MediaBoxSize A5 = new(420.0, 595.0);

    /// <summary>Same dimensions, swapped axes — convert portrait → landscape.</summary>
    public MediaBoxSize Landscape => new(HeightPts, WidthPts);
}
