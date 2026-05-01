// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Content;

/// <summary>
/// Operator-emission surface of a PDF content stream. Lifecycle methods (<c>Finish</c>) are
/// intentionally NOT on this interface — the owner of the stream (typically
/// <see cref="ContentStreamBuilder"/>) drives lifecycle so callbacks cannot break the
/// emit-then-finalize protocol. Direct callers who need lifecycle control use
/// <see cref="ContentStreamWriter"/> concretely.
/// </summary>
internal interface IContentStream
{
    // Graphics state
    void SaveState();
    void RestoreState();
    void ConcatMatrix(double a, double b, double c, double d, double e, double f);

    // Path construction
    void MoveTo(double x, double y);
    void LineTo(double x, double y);
    void Rectangle(double x, double y, double width, double height);
    void ClosePath();

    // Path painting
    void Fill();
    void Stroke();
    void EndPath();

    // Device color
    void SetFillRgb(double r, double g, double b);
    void SetStrokeRgb(double r, double g, double b);
    void SetFillGray(double gray);
    void SetStrokeGray(double gray);

    // Line attributes
    void SetLineWidth(double width);

    // Text
    void BeginText();
    void EndText();
    void SetFont(PdfName fontResourceName, double size);
    void MoveTextPosition(double tx, double ty);
    void MoveTextPositionAndSetLeading(double tx, double ty);
    void ShowText(ReadOnlySpan<byte> bytes);
    void ShowTextArray(ReadOnlySpan<TextArrayElement> elements);

    // XObject
    void PaintXObject(PdfName xobjectResourceName);

    // Marked content
    void BeginMarkedContent(PdfName tag);
    void BeginMarkedContentWithProperties(PdfName tag, PdfDictionary properties);
    void EndMarkedContent();
}
