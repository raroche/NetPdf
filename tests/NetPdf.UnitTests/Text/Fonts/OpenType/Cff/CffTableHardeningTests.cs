// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Post-Task-7 hardening: <see cref="CffTable.Parse"/> rejects malformed Top DICT offsets
/// (NaN / Infinity / fractional / negative / out-of-range) and rejects empty or
/// multi-entry Name INDEXes for OpenType-embedded CFF.
/// </summary>
public sealed class CffTableHardeningTests
{
    [Fact]
    public void Parse_rejects_charstrings_offset_that_is_not_an_integer()
    {
        var bytes = SyntheticCff.Build();
        // Patch the CharStrings 5-byte int offset to a real-number form (byte 30 +
        // nibble-encoded "1.5" + terminator). Five bytes total so the layout still aligns.
        var (charStringsStart, _) = LocateCharStringsOffsetField(bytes);
        bytes[charStringsStart] = 30;            // real marker
        bytes[charStringsStart + 1] = 0x1A;      // '1' '.'
        bytes[charStringsStart + 2] = 0x5F;      // '5' terminator
        // Pad to 5 bytes — terminator nibble means the parser stops reading after byte 2,
        // but the next bytes (op 17) still need to be intact.
        bytes[charStringsStart + 3] = 0x00;
        bytes[charStringsStart + 4] = 0x00;

        var ex = Assert.Throws<InvalidDataException>(() => CffTable.Parse(bytes));
        Assert.Contains("CharStrings", ex.Message);
    }

    [Fact]
    public void Parse_rejects_charstrings_offset_beyond_table_length()
    {
        var bytes = SyntheticCff.Build();
        var (charStringsStart, _) = LocateCharStringsOffsetField(bytes);
        // 5-byte int form with offset = bytes.Length + 1000 (out of range).
        bytes[charStringsStart] = 29;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(charStringsStart + 1, 4), bytes.Length + 1000);

        var ex = Assert.Throws<InvalidDataException>(() => CffTable.Parse(bytes));
        Assert.Contains("CharStrings", ex.Message);
    }

    [Fact]
    public void Parse_rejects_negative_charstrings_offset()
    {
        var bytes = SyntheticCff.Build();
        var (charStringsStart, _) = LocateCharStringsOffsetField(bytes);
        bytes[charStringsStart] = 29;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(charStringsStart + 1, 4), -1);

        var ex = Assert.Throws<InvalidDataException>(() => CffTable.Parse(bytes));
        Assert.Contains("CharStrings", ex.Message);
    }

    [Fact]
    public void Parse_rejects_charset_offset_with_non_integer_real_form()
    {
        var bytes = SyntheticCff.Build();
        var (charsetStart, _) = LocateCharsetOffsetField(bytes);
        bytes[charsetStart] = 30;
        bytes[charsetStart + 1] = 0x2A;          // '2' '.'
        bytes[charsetStart + 2] = 0x7F;          // '7' terminator
        bytes[charsetStart + 3] = 0x00;
        bytes[charsetStart + 4] = 0x00;

        var ex = Assert.Throws<InvalidDataException>(() => CffTable.Parse(bytes));
        Assert.Contains("charset", ex.Message);
    }

    [Fact]
    public void Parse_rejects_empty_name_index()
    {
        // Build a CFF where the Name INDEX is empty but everything else is present. We do this
        // by overwriting the synthetic Name INDEX header (3 bytes for count=1, offSize=1) with
        // a 2-byte empty INDEX — but this changes downstream offsets, so the parser will hit
        // the empty-Name check before any structural offset gets read.
        var minimal = BuildCffWithNameIndexCount(0);
        var ex = Assert.Throws<InvalidDataException>(() => CffTable.Parse(minimal));
        Assert.Contains("Name INDEX", ex.Message);
    }

    [Fact]
    public void Parse_rejects_multi_entry_name_index()
    {
        var minimal = BuildCffWithNameIndexCount(2);
        var ex = Assert.Throws<InvalidDataException>(() => CffTable.Parse(minimal));
        Assert.Contains("Name INDEX", ex.Message);
    }

    /// <summary>
    /// Locate the byte position of the 5-byte CharStrings offset inside the Top DICT object
    /// stream. Synthetic Top DICT layout from <see cref="SyntheticCff"/>: 5-byte charset
    /// offset, op 15, 5-byte CharStrings offset, op 17, 5-byte private size, 5-byte private
    /// offset, op 18.
    /// </summary>
    private static (int Start, int Length) LocateCharStringsOffsetField(byte[] cff)
    {
        var topDictStart = TopDictDataStart(cff);
        // [0..5)  charset offset
        // [5]     op 15
        // [6..11) CharStrings offset  ← target
        return (topDictStart + 6, 5);
    }

    private static (int Start, int Length) LocateCharsetOffsetField(byte[] cff)
    {
        var topDictStart = TopDictDataStart(cff);
        return (topDictStart, 5);
    }

    /// <summary>Locate the start of the Top DICT object data inside the CFF byte stream.</summary>
    private static int TopDictDataStart(byte[] cff)
    {
        // Header(4) + Name INDEX. Name INDEX layout from SyntheticCff: count=1, offSize=1,
        // offsets [1, 1 + nameLen], data = "SynthCff" (8 bytes) → 3 + 2 + 8 = 13 bytes.
        const int headerLen = 4;
        var nameIndexEnd = headerLen + 13;

        // Top DICT INDEX begins here: count=1, offSize=1, offsets [1, 1 + topDictPayloadLen],
        // data = the 23-byte Top DICT payload → 3 + 2 + 23 = 28 bytes total. Object data
        // starts at the offset-array end.
        const int topDictHeaderBytes = 3 + 2; // count + offSize + 2 offsets × 1 byte
        return nameIndexEnd + topDictHeaderBytes;
    }

    /// <summary>
    /// Build a minimal CFF byte stream with a Name INDEX containing exactly
    /// <paramref name="nameCount"/> entries — used to test the count-must-be-1 guard.
    /// We don't need a fully consistent CFF here because the Name INDEX guard fires before
    /// CharStrings / Charset parsing.
    /// </summary>
    private static byte[] BuildCffWithNameIndexCount(int nameCount)
    {
        // Header (4 bytes).
        var header = new byte[] { 1, 0, 4, 1 };

        // Name INDEX with `nameCount` entries (each is a single byte 'A').
        byte[] nameIndex;
        if (nameCount == 0)
        {
            nameIndex = new byte[] { 0, 0 };
        }
        else
        {
            var objects = new byte[nameCount][];
            for (var i = 0; i < nameCount; i++)
            {
                objects[i] = new byte[] { (byte)('A' + i) };
            }
            nameIndex = SyntheticCff.BuildIndex(objects);
        }

        // Trivial Top DICT INDEX (count=1, empty payload).
        var topDictIndex = SyntheticCff.BuildIndex(new[] { Array.Empty<byte>() });

        var output = new byte[header.Length + nameIndex.Length + topDictIndex.Length];
        var pos = 0;
        header.CopyTo(output.AsSpan(pos)); pos += header.Length;
        nameIndex.CopyTo(output.AsSpan(pos)); pos += nameIndex.Length;
        topDictIndex.CopyTo(output.AsSpan(pos));
        return output;
    }
}
