// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// ISO 32000-2 §7.3.10 — indirect reference. Emits <c>N G R</c> where N is the object
/// number and G is the generation. NetPdf uses generation 0 throughout v1 (no incremental updates).
/// </summary>
internal sealed class PdfIndirectRef : PdfObject
{
    public int ObjectNumber { get; }
    public int Generation { get; }

    public PdfIndirectRef(int objectNumber, int generation = 0)
    {
        if (objectNumber < 1) throw new ArgumentOutOfRangeException(nameof(objectNumber), "Object numbers start at 1.");
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteInteger(ObjectNumber);
        writer.WriteSpace();
        writer.WriteInteger(Generation);
        writer.WriteAscii(" R");
    }
}
