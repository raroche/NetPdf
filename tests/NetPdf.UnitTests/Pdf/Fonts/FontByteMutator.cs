// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Test-only helpers that mutate fields inside a built TTF byte stream by walking the
/// SFNT directory. Used by the post-Task-10 hardening tests to construct fonts with
/// specific malformed / non-default values (fsType bits, large advance widths, no
/// PostScript name) without rebuilding the whole synthetic-font fixture.
/// </summary>
internal static class FontByteMutator
{
    private const int SfntHeaderSize = 12;
    private const int RecordSize = 16;

    /// <summary>Set <c>OS/2.fsType</c> (uint16 at offset 8 of OS/2).</summary>
    public static void SetFsType(byte[] fontBytes, ushort newFsType)
    {
        OverwriteUInt16InTable(fontBytes, OpenTypeTags.Os2, fieldOffsetInTable: 8, value: newFsType);
    }

    /// <summary>Set the advance width of glyph <paramref name="glyphIndex"/> in the hmtx table.</summary>
    public static void SetHmtxAdvance(byte[] fontBytes, int glyphIndex, ushort newAdvance)
    {
        // Each long metric is 4 bytes (advance uint16 + lsb int16). Glyphs at index >=
        // numberOfHMetrics share the last advance and use the lsb-only trail; the
        // synthetic fixtures emit one long metric per glyph so we don't worry about that.
        var advanceFieldOffset = glyphIndex * 4;
        OverwriteUInt16InTable(fontBytes, OpenTypeTags.Hmtx, fieldOffsetInTable: advanceFieldOffset, value: newAdvance);
    }

    /// <summary>
    /// Replace the Name INDEX with one containing only a single FamilyName record (no
    /// PostScriptName), with the specified family text encoded as UTF-16BE on the
    /// Microsoft Unicode-BMP platform.
    /// </summary>
    public static byte[] ReplaceNameTableWithFamilyOnly(byte[] fontBytes, string familyName)
    {
        // Build a fresh name-table payload: 6-byte header + 1 record × 12 bytes + UTF-16BE family bytes.
        var encoded = System.Text.Encoding.BigEndianUnicode.GetBytes(familyName);
        var headerSize = 6 + 12;
        var newName = new byte[headerSize + encoded.Length];
        var span = newName.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);                          // format
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);                          // count
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)headerSize);         // storageOffset
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 3);                          // platformID = Windows
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 1);                         // encodingID = Unicode-BMP
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0x0409);                   // languageID = en-US
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 1);                        // nameID = FamilyName
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], (ushort)encoded.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..18], 0);                        // string offset
        encoded.CopyTo(span[headerSize..]);

        return ReplaceTable(fontBytes, OpenTypeTags.Name, newName);
    }

    /// <summary>
    /// Add a 32-byte synthetic <c>fpgm</c> (font program) table to the SFNT — used by
    /// hardening tests to verify hinting-table pass-through. Real fpgm data is opaque
    /// TrueType bytecode; the byte content doesn't matter for our pass-through check.
    /// </summary>
    public static byte[] AddFpgmTable(byte[] fontBytes, byte[] fpgmContent)
    {
        return ReplaceTable(fontBytes, OpenTypeTags.Fpgm, fpgmContent);
    }

    private static void OverwriteUInt16InTable(byte[] fontBytes, uint tag, int fieldOffsetInTable, ushort value)
    {
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(fontBytes.AsSpan(4, 2));
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = SfntHeaderSize + (i * RecordSize);
            var foundTag = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset, 4));
            if (foundTag != tag)
            {
                continue;
            }
            var tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset + 8, 4));
            BinaryPrimitives.WriteUInt16BigEndian(fontBytes.AsSpan(tableOffset + fieldOffsetInTable, 2), value);
            return;
        }
        throw new InvalidOperationException($"Table '{OpenTypeTags.ToAsciiString(tag)}' not found in font bytes.");
    }

    /// <summary>
    /// Rebuild the SFNT with <paramref name="tag"/>'s table replaced by <paramref name="newBytes"/>.
    /// Adds a new directory record if the tag was absent. Recomputes offsets and lengths;
    /// padding rules same as the original font (4-byte aligned).
    /// </summary>
    private static byte[] ReplaceTable(byte[] fontBytes, uint tag, byte[] newBytes)
    {
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(fontBytes.AsSpan(4, 2));
        var existingTables = new Dictionary<uint, byte[]>();
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = SfntHeaderSize + (i * RecordSize);
            var foundTag = BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset, 4));
            var tableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset + 8, 4));
            var tableLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fontBytes.AsSpan(recordOffset + 12, 4));
            existingTables[foundTag] = fontBytes.AsSpan(tableOffset, tableLength).ToArray();
        }
        existingTables[tag] = newBytes;

        var sortedTags = new uint[existingTables.Count];
        var idx = 0;
        foreach (var t in existingTables.Keys)
        {
            sortedTags[idx++] = t;
        }
        Array.Sort(sortedTags);

        var directorySize = RecordSize * sortedTags.Length;
        var firstTableOffset = SfntHeaderSize + directorySize;
        var offsets = new int[sortedTags.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < sortedTags.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignTo4(existingTables[sortedTags[i]].Length);
        }

        var output = new byte[cursor];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)sortedTags.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 128);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], (ushort)((sortedTags.Length * 16) - 128));

        var directoryCursor = SfntHeaderSize;
        for (var i = 0; i < sortedTags.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[directoryCursor..(directoryCursor + 4)], sortedTags[i]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 4)..(directoryCursor + 8)], 0);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 8)..(directoryCursor + 12)], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 12)..(directoryCursor + 16)], (uint)existingTables[sortedTags[i]].Length);
            directoryCursor += RecordSize;
        }
        for (var i = 0; i < sortedTags.Length; i++)
        {
            existingTables[sortedTags[i]].CopyTo(span[offsets[i]..]);
        }
        return output;
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
