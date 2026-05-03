// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

/// <summary>
/// Post-Task-6 hardening: subtable selector falls back when the preferred subtable is in
/// an unsupported format, and subtables are clamped to their declared length so a
/// malformed subtable can't read past its end into the next one.
/// </summary>
public sealed class CmapTableHardeningTests
{
    [Fact]
    public void Selector_skips_unsupported_format_and_falls_back_to_supported_one()
    {
        // Two encoding records, both Microsoft platform.
        //   Record 0: (3, 10) UCS-4, format 6 (unsupported)            ← preferred but unusable
        //   Record 1: (3,  1) BMP,   format 4 (supported, our fallback)
        // The selector must drop the unsupported record and pick the supported fallback.
        var format6 = BuildBareSubtable(format: 6, declaredLength: 10);
        var format4 = SyntheticFont.CmapBytes(); // includes a format-4 subtable inside

        // We compose a fresh cmap header pointing at two subtables we lay out manually.
        // Layout: header(4) + 2 records(8 each = 16) + format6 payload + format4 payload.
        var format4Subtable = ExtractFormat4Subtable(format4);
        var headerLen = 4 + (2 * 8);
        var subtable0Offset = headerLen;
        var subtable1Offset = headerLen + format6.Length;

        var bytes = new byte[headerLen + format6.Length + format4Subtable.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);                    // version
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 2);                    // numTables

        // Record 0 — preferred (UCS-4) but format 6.
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 10);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)subtable0Offset);

        // Record 1 — fallback (Microsoft Unicode BMP) format 4.
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..20], (uint)subtable1Offset);

        format6.CopyTo(span[subtable0Offset..]);
        format4Subtable.CopyTo(span[subtable1Offset..]);

        var cmap = CmapTable.Parse(bytes);
        Assert.Equal((ushort)4, cmap.SelectedFormat);
        Assert.Equal((ushort)1, cmap.SelectedEncodingId); // proves we fell back to the BMP record
    }

    [Fact]
    public void Selector_throws_when_no_supported_subtable_format_present()
    {
        // Single record pointing to a format-6 subtable. There's no fallback, so parse must fail.
        var format6 = BuildBareSubtable(format: 6, declaredLength: 10);
        var headerLen = 4 + 8;
        var bytes = new byte[headerLen + format6.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)headerLen);
        format6.CopyTo(span[headerLen..]);

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(bytes));
    }

    [Fact]
    public void Subtable_with_declared_length_exceeding_available_bytes_is_rejected()
    {
        // A format-4 subtable whose declared length runs past the end of the cmap table.
        var format4 = ExtractFormat4Subtable(SyntheticFont.CmapBytes());
        // Inflate the declared length so it overruns.
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(2, 2), (ushort)(format4.Length + 100));

        var headerLen = 4 + 8;
        var bytes = new byte[headerLen + format4.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)headerLen);
        format4.CopyTo(span[headerLen..]);

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(bytes));
    }

    [Fact]
    public void Subtable_with_zero_declared_length_is_rejected()
    {
        var format4 = ExtractFormat4Subtable(SyntheticFont.CmapBytes());
        BinaryPrimitives.WriteUInt16BigEndian(format4.AsSpan(2, 2), 0);

        var headerLen = 4 + 8;
        var bytes = new byte[headerLen + format4.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)headerLen);
        format4.CopyTo(span[headerLen..]);

        Assert.Throws<InvalidDataException>(() => CmapTable.Parse(bytes));
    }

    [Fact]
    public void Format4_parser_does_not_read_past_declared_length_into_following_subtable_bytes()
    {
        // Lay out two format-4 subtables back to back. The first has a declared length that
        // covers only its own bytes; the second contains a sentinel byte pattern. If the
        // parser ever read past the first subtable's declared length, it would find the
        // sentinel and produce a different result than the clamped parse.
        var format4 = ExtractFormat4Subtable(SyntheticFont.CmapBytes());
        var sentinel = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        var headerLen = 4 + 8;
        var bytes = new byte[headerLen + format4.Length + sentinel.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..12], (uint)headerLen);
        format4.CopyTo(span[headerLen..]);
        sentinel.CopyTo(span[(headerLen + format4.Length)..]);

        // If clamping is honored, this still produces the same mappings as the clamped subtable.
        var cmap = CmapTable.Parse(bytes);
        Assert.Equal((ushort)1, cmap.GetGlyphId('A'));
        Assert.Equal((ushort)2, cmap.GetGlyphId('B'));
        // Sentinel bytes (0xFFFF) should never be mapped — that would prove the parser overran.
        Assert.Equal((ushort)0, cmap.GetGlyphId(0xFFFE));
    }

    /// <summary>
    /// Build a minimal, well-formed cmap subtable with the given <paramref name="format"/>
    /// and <paramref name="declaredLength"/>. Used to generate "unsupported format" subtables
    /// that nonetheless have a valid format byte / length field for the selector to peek at.
    /// </summary>
    private static byte[] BuildBareSubtable(ushort format, int declaredLength)
    {
        if (declaredLength < 4)
        {
            throw new ArgumentException("Subtable needs at least 4 bytes (format + length).", nameof(declaredLength));
        }
        var bytes = new byte[declaredLength];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), format);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), (ushort)declaredLength);
        return bytes;
    }

    /// <summary>Pull the format-4 subtable bytes out of <see cref="SyntheticFont.CmapBytes"/>.</summary>
    private static byte[] ExtractFormat4Subtable(byte[] cmap)
    {
        // Header layout: version(2) numTables(2) [record(8)]
        // Record(0): platform(2) encoding(2) offset(4)
        var subtableOffset = BinaryPrimitives.ReadUInt32BigEndian(cmap.AsSpan(8, 4));
        var subtableLength = BinaryPrimitives.ReadUInt16BigEndian(cmap.AsSpan((int)subtableOffset + 2, 2));
        return cmap.AsSpan((int)subtableOffset, subtableLength).ToArray();
    }
}
