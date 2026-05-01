// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.3 — real (floating-point) object. Emitted in canonical fixed-point form
/// with up to 6 fractional digits; NaN and Infinity are rejected per spec.
/// </summary>
internal sealed class PdfReal : PdfObject
{
    public double Value { get; }

    public PdfReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentException("PDF reals must be finite (NaN and Infinity are not permitted).", nameof(value));
        }
        Value = value;
    }

    public override void WriteTo(PdfWriter writer) => writer.WriteReal(Value);
}
