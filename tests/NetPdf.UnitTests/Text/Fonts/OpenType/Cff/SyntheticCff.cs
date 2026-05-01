// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Builds minimal-but-valid CFF v1 byte streams for parser tests. Produces a 3-glyph
/// non-CID font (.notdef + two real glyphs with stub charstrings) plus accessor methods
/// for individual structures (header, INDEX, DICT, charset) used by per-class unit tests.
/// </summary>
internal static class SyntheticCff
{
    public const int NumGlyphs = 3;
    public const string FontName = "SynthCff";

    /// <summary>
    /// Build a complete CFF v1 byte stream layout:
    /// header → Name INDEX → Top DICT INDEX → String INDEX → Global Subr INDEX →
    /// Charset → CharStrings INDEX → Private DICT.
    /// Top DICT carries the charset offset (op 15) and CharStrings offset (op 17).
    /// </summary>
    public static byte[] Build()
    {
        // 1. Header (4 bytes): major=1, minor=0, hdrSize=4, offSize=2.
        var header = new byte[] { 1, 0, 4, 2 };

        // 2. Name INDEX with one entry: "SynthCff".
        var nameIndex = BuildIndex(new[] { Encoding.Latin1.GetBytes(FontName) });

        // 3. Charset (format 0): one byte format + 2 bytes × (NumGlyphs - 1) SIDs = 5 bytes.
        // Map glyph 1 → SID 391 (custom), glyph 2 → SID 392.
        var charset = new byte[1 + ((NumGlyphs - 1) * 2)];
        charset[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(charset.AsSpan(1, 2), 391);
        BinaryPrimitives.WriteUInt16BigEndian(charset.AsSpan(3, 2), 392);

        // 4. CharStrings INDEX: one stub charstring per glyph. Single-byte 0x0E ("endchar")
        //    is a valid Type 2 charstring representing an empty glyph.
        var charString = new byte[] { 0x0E };
        var charStrings = BuildIndex(new[] { charString, charString, charString });

        // 5. String INDEX (empty) + Global Subr INDEX (empty).
        var stringIndex = BuildIndex(Array.Empty<byte[]>());
        var globalSubrIndex = BuildIndex(Array.Empty<byte[]>());

        // 6. Compute layout. We need to know charset + charstrings offsets so the Top DICT
        //    can reference them. Layout order:
        //      header (4)
        //    + nameIndex (~)
        //    + topDictIndex (~) ← built last because it depends on later offsets
        //    + stringIndex (~)
        //    + globalSubrIndex (~)
        //    + charset
        //    + charStrings
        //    + privateDict
        //
        // To break the circularity we choose a fixed-width Top DICT (5-byte ints for offsets)
        // so its size is predictable.

        // Build a Top DICT with fixed-width 5-byte int operands so offsets can be patched.
        // Reserve 24 bytes for a Top DICT containing:
        //   <charset offset>(5) 15(1) <charStrings offset>(5) 17(1)
        //   <private size>(5) <private offset>(5) 18(1) (1 escape) → ~22 bytes
        // We'll just construct it and measure.
        var privateDict = BuildPrivateDict();

        // Place-holder Top DICT bytes (will patch offsets after layout is computed).
        var topDictPayload = BuildTopDictPlaceholder();
        var topDictIndex = BuildIndex(new[] { topDictPayload });

        // Now compute final offsets.
        var headerLen = header.Length;
        var nameIndexLen = nameIndex.Length;
        var topDictIndexLen = topDictIndex.Length;
        var stringIndexLen = stringIndex.Length;
        var globalSubrIndexLen = globalSubrIndex.Length;

        var charsetOffset = headerLen + nameIndexLen + topDictIndexLen + stringIndexLen + globalSubrIndexLen;
        var charStringsOffset = charsetOffset + charset.Length;
        var privateOffset = charStringsOffset + charStrings.Length;
        var privateSize = privateDict.Length;

        // Patch the placeholder Top DICT with real offsets.
        PatchTopDict(topDictIndex, charsetOffset, charStringsOffset, privateOffset, privateSize);

        // 7. Concatenate everything.
        var totalSize = privateOffset + privateSize;
        var output = new byte[totalSize];
        var span = output.AsSpan();
        var pos = 0;
        header.CopyTo(span[pos..]); pos += headerLen;
        nameIndex.CopyTo(span[pos..]); pos += nameIndexLen;
        topDictIndex.CopyTo(span[pos..]); pos += topDictIndexLen;
        stringIndex.CopyTo(span[pos..]); pos += stringIndexLen;
        globalSubrIndex.CopyTo(span[pos..]); pos += globalSubrIndexLen;
        charset.CopyTo(span[pos..]); pos += charset.Length;
        charStrings.CopyTo(span[pos..]); pos += charStrings.Length;
        privateDict.CopyTo(span[pos..]);

        return output;
    }

    /// <summary>Build a CFF INDEX from a sequence of object byte buffers, using offSize=1.</summary>
    public static byte[] BuildIndex(byte[][] objects)
    {
        if (objects.Length == 0)
        {
            // Empty INDEX = just count(uint16) = 0.
            return new byte[] { 0, 0 };
        }
        var totalDataLen = 0;
        foreach (var o in objects)
        {
            totalDataLen += o.Length;
        }
        var offSize = totalDataLen < 0xFE ? 1 : totalDataLen < 0xFFFE ? 2 : 4;

        var headerLen = 3 + ((objects.Length + 1) * offSize);
        var bytes = new byte[headerLen + totalDataLen];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], (ushort)objects.Length);
        span[2] = (byte)offSize;

        // Write offsets (1-based, the spec's quirk).
        var runningOffset = 1u;
        WriteOffset(span.Slice(3, offSize), runningOffset, offSize);
        for (var i = 0; i < objects.Length; i++)
        {
            runningOffset += (uint)objects[i].Length;
            WriteOffset(span.Slice(3 + ((i + 1) * offSize), offSize), runningOffset, offSize);
        }

        // Concatenate object data after the offset array.
        var dataStart = headerLen;
        foreach (var o in objects)
        {
            o.CopyTo(span[dataStart..]);
            dataStart += o.Length;
        }
        return bytes;
    }

    /// <summary>Encode a CFF DICT 5-byte integer operand (byte0=29 + int32 BE).</summary>
    public static byte[] EncodeFiveByteInt(int value)
    {
        var bytes = new byte[5];
        bytes[0] = 29;
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(1, 4), value);
        return bytes;
    }

    private static byte[] BuildPrivateDict()
    {
        // Minimal Private DICT: BlueValues empty, default subroutine count.
        // Just emit operator 6 (BlueValues) with empty operand stack — a Private DICT can be
        // empty; many real fonts have at least BlueValues but it's not strictly required.
        return Array.Empty<byte>();
    }

    /// <summary>
    /// Build a Top DICT with fixed-width int operands so we can patch offsets without
    /// changing the encoded size:
    ///   [5-byte charset offset] op 15 (charset)
    ///   [5-byte charStrings offset] op 17 (CharStrings)
    ///   [5-byte private size] [5-byte private offset] op 18 (Private)
    /// </summary>
    private static byte[] BuildTopDictPlaceholder()
    {
        // 5 + 1 + 5 + 1 + 5 + 5 + 1 = 23 bytes.
        var bytes = new byte[23];
        // We'll patch the offsets later. For now leave zeros, which is technically invalid
        // but the placeholder gets overwritten before any parser sees it.
        var pos = 0;
        EncodeFiveByteInt(0).CopyTo(bytes.AsSpan(pos)); pos += 5;
        bytes[pos++] = 15;
        EncodeFiveByteInt(0).CopyTo(bytes.AsSpan(pos)); pos += 5;
        bytes[pos++] = 17;
        EncodeFiveByteInt(0).CopyTo(bytes.AsSpan(pos)); pos += 5;
        EncodeFiveByteInt(0).CopyTo(bytes.AsSpan(pos)); pos += 5;
        bytes[pos] = 18;
        return bytes;
    }

    private static void PatchTopDict(byte[] topDictIndex, int charsetOffset, int charStringsOffset, int privateOffset, int privateSize)
    {
        // The Top DICT INDEX header was built with one object. The object data starts at:
        //   3 (count + offSize) + 2*offSize (one offset pair, since count=1 → 2 offsets).
        // offSize was chosen by BuildIndex; for a 23-byte payload it's 1.
        var offSize = topDictIndex[2];
        var dataStart = 3 + ((1 + 1) * offSize);

        // Inside the Top DICT object, the layout from BuildTopDictPlaceholder is:
        //   [0..5)   charset offset (5-byte int operand)
        //   [5]      op 15
        //   [6..11)  charStrings offset
        //   [11]     op 17
        //   [12..17) private size
        //   [17..22) private offset
        //   [22]     op 18
        var dict = topDictIndex.AsSpan(dataStart);
        EncodeFiveByteInt(charsetOffset).CopyTo(dict[0..]);
        EncodeFiveByteInt(charStringsOffset).CopyTo(dict[6..]);
        EncodeFiveByteInt(privateSize).CopyTo(dict[12..]);
        EncodeFiveByteInt(privateOffset).CopyTo(dict[17..]);
    }

    private static void WriteOffset(Span<byte> dest, uint value, int width)
    {
        switch (width)
        {
            case 1: dest[0] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)value); break;
            case 3:
                dest[0] = (byte)(value >> 16);
                dest[1] = (byte)(value >> 8);
                dest[2] = (byte)value;
                break;
            case 4: BinaryPrimitives.WriteUInt32BigEndian(dest, value); break;
            default: throw new ArgumentOutOfRangeException(nameof(width), width, "Invalid offset width.");
        }
    }
}
