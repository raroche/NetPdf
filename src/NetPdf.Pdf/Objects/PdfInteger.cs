// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>ISO 32000-2 §7.3.3 — integer object. Emits the decimal ASCII representation.</summary>
internal sealed class PdfInteger : PdfObject
{
    public long Value { get; }

    public PdfInteger(long value) => Value = value;

    public override void WriteTo(PdfWriter writer) => writer.WriteInteger(Value);

    public static implicit operator PdfInteger(int value) => new(value);
    public static implicit operator PdfInteger(long value) => new(value);
}
