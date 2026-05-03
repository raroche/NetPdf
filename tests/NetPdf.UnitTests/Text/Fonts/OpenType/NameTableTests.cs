// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class NameTableTests
{
    [Fact]
    public void Parse_synthetic_font_returns_postscript_name()
    {
        var name = NameTable.Parse(SyntheticFont.NameBytes());
        Assert.Equal((ushort)0, name.Format);
        Assert.Single(name.Records);
        Assert.Equal("Synth-Test", name.PostScriptName);
    }

    [Fact]
    public void GetName_prefers_windows_record_over_macintosh()
    {
        // Build a name table with two records for the same nameID — Mac (postscript) and Windows (postscript).
        // Expected: GetName returns the Windows-Unicode record's text.
        const string winText = "Win-PS";
        const string macText = "Mac-PS";
        var winBytes = Encoding.BigEndianUnicode.GetBytes(winText);
        var macBytes = Encoding.ASCII.GetBytes(macText);

        var headerSize = 6 + (12 * 2);
        var bytes = new byte[headerSize + winBytes.Length + macBytes.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);                          // format
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 2);                          // count
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)headerSize);         // storageOffset

        // Record 0: Mac
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 1);                          // platformID Mac
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 0);                         // encoding Roman
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], 0);                        // language English
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], 6);                        // nameID PostScript
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], (ushort)macBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..18], 0);                        // offset = 0

        // Record 1: Windows
        BinaryPrimitives.WriteUInt16BigEndian(span[18..20], 3);                        // platformID Windows
        BinaryPrimitives.WriteUInt16BigEndian(span[20..22], 1);                        // encoding Unicode-BMP
        BinaryPrimitives.WriteUInt16BigEndian(span[22..24], 0x0409);                   // en-US
        BinaryPrimitives.WriteUInt16BigEndian(span[24..26], 6);                        // nameID PostScript
        BinaryPrimitives.WriteUInt16BigEndian(span[26..28], (ushort)winBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..30], (ushort)macBytes.Length);  // offset = after Mac string

        macBytes.CopyTo(span[headerSize..]);
        winBytes.CopyTo(span[(headerSize + macBytes.Length)..]);

        var name = NameTable.Parse(bytes);
        Assert.Equal(winText, name.PostScriptName);
    }

    [Fact]
    public void Parse_truncated_record_skips_text_but_does_not_throw()
    {
        // Take the synthetic name bytes and chop off the trailing string.
        var bytes = SyntheticFont.NameBytes();
        Array.Resize(ref bytes, bytes.Length - 4); // drop a chunk of the encoded string

        var name = NameTable.Parse(bytes);
        // Record was written but its text fell out of range — the parser preserves the record
        // with Text = null rather than failing.
        Assert.Single(name.Records);
        Assert.Null(name.Records[0].Text);
    }

    [Fact]
    public void Parse_throws_on_unknown_format()
    {
        var bytes = SyntheticFont.NameBytes();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0, 2), 7);
        Assert.Throws<InvalidDataException>(() => NameTable.Parse(bytes));
    }
}
