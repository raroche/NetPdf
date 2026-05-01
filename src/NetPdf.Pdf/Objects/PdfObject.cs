// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// Base type of every PDF object NetPdf emits. Subclasses produce a byte representation
/// conforming to ISO 32000-2:2020 §7.3 via <see cref="WriteTo"/>, and expose their direct
/// child <see cref="PdfObject"/>s via <see cref="EnumerateChildren"/> so graph-level
/// operations (preflight validation, dead-object detection, future xref-stream walks)
/// don't need subtype switches.
/// </summary>
internal abstract class PdfObject
{
    public abstract void WriteTo(PdfWriter writer);

    /// <summary>
    /// The direct child objects of this object (array elements, dictionary values,
    /// stream sub-dictionary). Default: none. Override in container types.
    /// </summary>
    public virtual IEnumerable<PdfObject> EnumerateChildren() => Array.Empty<PdfObject>();
}
