// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// Base type of every PDF object NetPdf emits. Subclasses produce a byte representation
/// conforming to ISO 32000-2:2020 §7.3 via <see cref="WriteTo"/>.
/// </summary>
internal abstract class PdfObject
{
    public abstract void WriteTo(PdfWriter writer);
}
