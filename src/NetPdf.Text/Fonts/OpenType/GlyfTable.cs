// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>glyf</c> table accessor (OpenType §"glyf"). Phase 1 exposes the per-glyph
/// raw bytes addressed via <see cref="LocaTable.Offsets"/>; the deep parse of glyph
/// outlines (simple vs composite glyph headers, contours, instructions) lives with the
/// subsetter (Phase 1 Task 8) which is the only consumer that needs it before Phase 3.
/// </summary>
internal sealed class GlyfTable
{
    /// <summary>The raw <c>glyf</c> table bytes. Borrowed slice over the original font buffer; lifetime tied to the owning <see cref="OpenTypeFont"/>.</summary>
    public required ReadOnlyMemory<byte> RawBytes { get; init; }

    private readonly LocaTable _loca;

    public GlyfTable(LocaTable loca)
    {
        ArgumentNullException.ThrowIfNull(loca);
        _loca = loca;
    }

    /// <summary>
    /// Slice the <c>glyf</c> bytes for glyph <paramref name="glyphIndex"/> using
    /// <see cref="LocaTable"/>. Returns an empty span for zero-length glyphs (e.g., space).
    /// </summary>
    public ReadOnlySpan<byte> GetGlyphBytes(int glyphIndex)
    {
        if ((uint)glyphIndex >= (uint)_loca.NumGlyphs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(glyphIndex), glyphIndex,
                $"Glyph index {glyphIndex} out of range (numGlyphs = {_loca.NumGlyphs}).");
        }
        var offset = _loca.Offsets[glyphIndex];
        var length = _loca.Offsets[glyphIndex + 1] - offset;
        if (length == 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }
        if ((long)offset + length > RawBytes.Length)
        {
            throw new InvalidDataException(
                $"glyf: glyph {glyphIndex} at offset {offset} length {length} extends past glyf end ({RawBytes.Length}).");
        }
        return RawBytes.Span.Slice((int)offset, (int)length);
    }

    /// <summary>True if glyph <paramref name="glyphIndex"/> has zero bytes (typically whitespace glyphs).</summary>
    public bool IsEmptyGlyph(int glyphIndex) => _loca.GetGlyphLength(glyphIndex) == 0;
}
