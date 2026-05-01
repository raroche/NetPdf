// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Builds a minimal-but-valid OpenType / CFF (OTF) font byte stream by wrapping the
/// <see cref="SyntheticCff"/> CFF byte stream in an SFNT envelope. Reuses the per-table
/// builders from <see cref="NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont"/> for the
/// 8 metric / metadata tables (<c>head</c>, <c>hhea</c>, <c>hmtx</c>, <c>maxp</c>,
/// <c>name</c>, <c>OS/2</c>, <c>post</c>, <c>cmap</c>); replaces <c>loca</c> + <c>glyf</c>
/// with the synthetic <c>CFF </c> table.
/// </summary>
internal static class SyntheticOtf
{
    public static byte[] Build()
    {
        var head = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HeadBytes();
        var hhea = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HheaBytes();
        var maxp = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.MaxpBytes();
        var os2 = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.Os2Bytes();
        var post = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.PostBytes();
        var name = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.NameBytes();
        var hmtx = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HmtxBytes();
        var cmap = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.CmapBytes();
        var cff = SyntheticCff.Build();

        // OTF tables in canonical sorted-tag order. No loca / glyf — those are TTF-only.
        var tables = new (uint Tag, byte[] Bytes)[]
        {
            (0x4F532F32u, os2),  // OS/2
            (0x43464620u, cff),  // CFF
            (0x636D6170u, cmap), // cmap
            (0x68656164u, head), // head
            (0x68686561u, hhea), // hhea
            (0x686D7478u, hmtx), // hmtx
            (0x6D617870u, maxp), // maxp
            (0x6E616D65u, name), // name
            (0x706F7374u, post), // post
        };
        Array.Sort(tables, (a, b) => a.Tag.CompareTo(b.Tag));

        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var firstTableOffset = sfntHeaderSize + (recordSize * tables.Length);

        var offsets = new int[tables.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignTo4(tables[i].Bytes.Length);
        }
        var totalSize = cursor;

        var output = new byte[totalSize];
        var writer = output.AsSpan();

        // SFNT header — sfntVersion = 'OTTO' for CFF outlines.
        BinaryPrimitives.WriteUInt32BigEndian(writer[..4], 0x4F54544Fu);
        BinaryPrimitives.WriteUInt16BigEndian(writer[4..6], (ushort)tables.Length);
        BinaryPrimitives.WriteUInt16BigEndian(writer[6..8], 128);
        BinaryPrimitives.WriteUInt16BigEndian(writer[8..10], 3);
        BinaryPrimitives.WriteUInt16BigEndian(writer[10..12], (ushort)((tables.Length * 16) - 128));

        var directoryCursor = sfntHeaderSize;
        for (var i = 0; i < tables.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(writer[directoryCursor..(directoryCursor + 4)], tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(directoryCursor + 4)..(directoryCursor + 8)], 0);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(directoryCursor + 8)..(directoryCursor + 12)], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(writer[(directoryCursor + 12)..(directoryCursor + 16)], (uint)tables[i].Bytes.Length);
            directoryCursor += recordSize;
        }

        for (var i = 0; i < tables.Length; i++)
        {
            tables[i].Bytes.CopyTo(writer[offsets[i]..]);
        }

        return output;
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
