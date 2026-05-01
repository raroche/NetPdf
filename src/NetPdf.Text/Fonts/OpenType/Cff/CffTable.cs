// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType.Cff;

/// <summary>
/// Parsed <c>CFF </c> table. Provides structural access — header, indices, charset,
/// per-glyph charstring bytes — that the Phase 1 subsetter (Task 8) and embedder (Task 10)
/// build on. Deep Type 2 charstring decoding (operand parsing, subroutine traversal) is
/// the subsetter's job; the parser here surfaces byte-level access.
/// </summary>
/// <remarks>
/// <para>
/// CFF v1 layout (Adobe Technical Note #5176 §"3 Top-Level Organization"):
/// header → Name INDEX → Top DICT INDEX → String INDEX → Global Subr INDEX → encodings →
/// charsets → FDSelect (CID-keyed) → CharStrings INDEX → Font DICT INDEX → Private DICT(s)
/// → Local Subr INDEX(es) → Copyright + trademark notices.
/// </para>
/// <para>
/// CID-keyed fonts (presence of <c>ROS</c> in Top DICT) replace the per-glyph SID
/// interpretation with CIDs — different downstream meaning, same byte layout. We expose
/// <see cref="IsCidKeyed"/> so consumers can distinguish.
/// </para>
/// </remarks>
internal sealed class CffTable
{
    public required CffHeader Header { get; init; }
    public required CffIndex NameIndex { get; init; }
    public required CffIndex TopDictIndex { get; init; }
    public required CffIndex StringIndex { get; init; }
    public required CffIndex GlobalSubrIndex { get; init; }
    public required CffIndex CharStringsIndex { get; init; }
    public required CffCharset Charset { get; init; }

    /// <summary>Decoded Top DICT (operator → operand stack). Always taken from <c>TopDictIndex[0]</c>.</summary>
    public required IReadOnlyDictionary<int, double[]> TopDict { get; init; }

    /// <summary>Font name from <see cref="NameIndex"/> (UTF-8 / ASCII, decoded as Latin1 since CFF restricts names to ASCII).</summary>
    public required string FontName { get; init; }

    /// <summary>True when the font is CID-keyed (presence of the <c>ROS</c> operator in Top DICT).</summary>
    public required bool IsCidKeyed { get; init; }

    public int NumGlyphs => CharStringsIndex.Count;

    public ReadOnlySpan<byte> GetCharStringBytes(int glyphIndex) => CharStringsIndex.GetObjectBytes(glyphIndex);

    public ushort GetGlyphSidOrCid(int glyphIndex) => Charset.GetGlyphSidOrCid(glyphIndex);

    public static CffTable Parse(ReadOnlyMemory<byte> tableBytes)
    {
        if (tableBytes.IsEmpty)
        {
            throw new ArgumentException("CFF: tableBytes must not be empty.", nameof(tableBytes));
        }
        var span = tableBytes.Span;

        var header = CffHeader.Parse(span);

        int cursor = header.HdrSize;
        var nameIndex = CffIndex.Parse(span[cursor..], tableBytes, cursor);
        cursor += nameIndex.TotalSize;

        var topDictIndex = CffIndex.Parse(span[cursor..], tableBytes, cursor);
        cursor += topDictIndex.TotalSize;
        if (topDictIndex.Count != 1)
        {
            // CFF v1 allows multiple Top DICTs only inside FontSets — single-font tables (the
            // CFF embedded inside an OpenType file) always carry exactly one Top DICT.
            throw new InvalidDataException(
                $"CFF: Top DICT INDEX must contain exactly 1 entry for an OpenType-embedded font; got {topDictIndex.Count}.");
        }
        var topDict = CffDict.Parse(topDictIndex.GetObjectBytes(0));

        var stringIndex = CffIndex.Parse(span[cursor..], tableBytes, cursor);
        cursor += stringIndex.TotalSize;

        var globalSubrIndex = CffIndex.Parse(span[cursor..], tableBytes, cursor);
        // cursor advanced; remaining structures (charset, FDSelect, CharStrings, Private,
        // Local Subr, Font DICT INDEX) are reached via Top DICT pointers, not sequentially.
        cursor += globalSubrIndex.TotalSize;

        if (!topDict.TryGetValue(CffDict.OpCharStrings, out var charStringsOperands)
            || charStringsOperands.Length != 1)
        {
            throw new InvalidDataException("CFF Top DICT: missing or malformed CharStrings operator (17).");
        }
        var charStringsOffset = (int)charStringsOperands[0];
        if ((uint)charStringsOffset >= (uint)tableBytes.Length)
        {
            throw new InvalidDataException(
                $"CFF Top DICT: CharStrings offset {charStringsOffset} exceeds table length {tableBytes.Length}.");
        }
        var charStringsIndex = CffIndex.Parse(span[charStringsOffset..], tableBytes, charStringsOffset);

        // Charset: when the operator is absent, CFF defaults to the predefined ISOAdobe
        // charset (Top DICT default 0). The predefined charsets are only meaningful for
        // legacy non-CID Type 1 use; OpenType-embedded fonts pretty much always provide an
        // explicit charset offset. Phase 1 requires an explicit charset to keep the parser
        // honest — predefined-charset fallback is a Task 8/10 concern when needed.
        if (!topDict.TryGetValue(CffDict.OpCharset, out var charsetOperands)
            || charsetOperands.Length != 1)
        {
            throw new InvalidDataException(
                "CFF Top DICT: missing explicit charset offset (15). " +
                "Phase 1 requires an explicit charset; predefined-charset fallback is deferred.");
        }
        var charsetOffset = (int)charsetOperands[0];
        if ((uint)charsetOffset >= (uint)tableBytes.Length)
        {
            throw new InvalidDataException(
                $"CFF Top DICT: charset offset {charsetOffset} exceeds table length {tableBytes.Length}.");
        }
        var charset = CffCharset.Parse(span[charsetOffset..], charStringsIndex.Count);

        var isCidKeyed = topDict.ContainsKey(CffDict.OpRos);
        var fontName = nameIndex.Count == 0
            ? string.Empty
            : System.Text.Encoding.Latin1.GetString(nameIndex.GetObjectBytes(0));

        return new CffTable
        {
            Header = header,
            NameIndex = nameIndex,
            TopDictIndex = topDictIndex,
            StringIndex = stringIndex,
            GlobalSubrIndex = globalSubrIndex,
            CharStringsIndex = charStringsIndex,
            Charset = charset,
            TopDict = topDict,
            FontName = fontName,
            IsCidKeyed = isCidKeyed,
        };
    }
}
