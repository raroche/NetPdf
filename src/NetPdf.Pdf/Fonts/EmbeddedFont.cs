// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// All the PDF objects that together make one embedded subset font: the Type 0 wrapper
/// dictionary (the one a content stream's <c>/Font</c> resource points at), the
/// CIDFontType2 child dictionary (TTF outlines), the FontDescriptor dictionary, and the
/// two streams (<c>FontFile2</c> with the SFNT bytes and <c>ToUnicode</c> with the CMap
/// from Task 9). Children are direct dictionary references; the document writer
/// (Phase 1 Task 22) replaces the structural cross-references — <c>/FontDescriptor</c>,
/// <c>/DescendantFonts[0]</c>, <c>/FontFile2</c>, <c>/ToUnicode</c> — with
/// <see cref="PdfIndirectRef"/> values when each child is allocated an indirect-object
/// number.
/// </summary>
internal sealed class EmbeddedFont
{
    public required string SubsetBaseFontName { get; init; }
    public required PdfDictionary Type0FontDictionary { get; init; }
    public required PdfDictionary CidFontDictionary { get; init; }
    public required PdfDictionary FontDescriptorDictionary { get; init; }
    public required PdfStream FontFile2Stream { get; init; }
    public required PdfStream ToUnicodeStream { get; init; }
}
