// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Rendering;

/// <summary>
/// The CSS-pixel → PDF-point conversion applied at the emit boundary. NetPdf's
/// canonical layout unit is the CSS pixel (1 px at 96 DPI); PDF user space is
/// points (1 pt = 1/72 in). At the reference 96 DPI a CSS pixel is therefore
/// exactly <c>72 / 96 = 0.75</c> pt. Layout / the box tree work entirely in CSS
/// px; only the painter + the page-size → <c>/MediaBox</c> mapping cross into
/// points, both via this single constant so the factor lives in one place.
/// </summary>
internal static class PdfUnits
{
    /// <summary>PDF points per CSS pixel — <c>72 / 96 = 0.75</c>. The exact
    /// rational form documents the 96-DPI / 72-pt-per-inch derivation rather
    /// than hard-coding the decimal.</summary>
    public const double PointsPerPixel = 72.0 / 96.0;

    /// <summary>Convert a CSS-pixel length to PDF points.</summary>
    public static double PxToPt(double px) => px * PointsPerPixel;
}
