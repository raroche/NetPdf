// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf;

/// <summary>A path-construction operator kind for <see cref="PdfPage.BeginPathClip"/>
/// (<c>m</c> / <c>l</c> / <c>c</c> / <c>h</c>).</summary>
public enum PdfPathVerb : byte { MoveTo, LineTo, CurveTo, Close }

/// <summary>One path segment for <see cref="PdfPage.BeginPathClip"/>: a verb plus up to three points (PDF
/// points, bottom-left origin). <see cref="PdfPathVerb.MoveTo"/> / <see cref="PdfPathVerb.LineTo"/> use
/// (<see cref="X1"/>, <see cref="Y1"/>); <see cref="PdfPathVerb.CurveTo"/> uses all three (two control points
/// + the endpoint); <see cref="PdfPathVerb.Close"/> uses none.</summary>
public readonly record struct PdfPathSegment(
    PdfPathVerb Verb, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    public static PdfPathSegment Move(double x, double y) => new(PdfPathVerb.MoveTo, x, y, 0, 0, 0, 0);
    public static PdfPathSegment Line(double x, double y) => new(PdfPathVerb.LineTo, x, y, 0, 0, 0, 0);
    public static PdfPathSegment Curve(double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(PdfPathVerb.CurveTo, x1, y1, x2, y2, x3, y3);
    public static PdfPathSegment Close => new(PdfPathVerb.Close, 0, 0, 0, 0, 0, 0);
}
