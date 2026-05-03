// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType.Cff;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType.Cff;

/// <summary>
/// Integration tests for the CFF parser: build a synthetic CFF byte stream and assert
/// the orchestrator wires every structure (header → indices → charset → CharStrings)
/// together correctly.
/// </summary>
public sealed class CffTableTests
{
    [Fact]
    public void Parse_synthetic_cff_returns_header_and_font_name()
    {
        var bytes = SyntheticCff.Build();
        var cff = CffTable.Parse(bytes);

        Assert.Equal((byte)1, cff.Header.Major);
        Assert.Equal((byte)0, cff.Header.Minor);
        Assert.Equal(SyntheticCff.FontName, cff.FontName);
    }

    [Fact]
    public void Parse_resolves_charstrings_for_each_glyph()
    {
        var bytes = SyntheticCff.Build();
        var cff = CffTable.Parse(bytes);

        Assert.Equal(SyntheticCff.NumGlyphs, cff.NumGlyphs);
        for (var i = 0; i < cff.NumGlyphs; i++)
        {
            var charString = cff.GetCharStringBytes(i);
            Assert.False(charString.IsEmpty);
            Assert.Equal((byte)0x0E, charString[0]); // endchar
        }
    }

    [Fact]
    public void Parse_resolves_charset_with_implicit_notdef()
    {
        var bytes = SyntheticCff.Build();
        var cff = CffTable.Parse(bytes);

        Assert.Equal((ushort)0, cff.GetGlyphSidOrCid(0)); // .notdef
        Assert.Equal((ushort)391, cff.GetGlyphSidOrCid(1));
        Assert.Equal((ushort)392, cff.GetGlyphSidOrCid(2));
    }

    [Fact]
    public void Parse_marks_synthetic_font_as_non_cid_keyed()
    {
        var bytes = SyntheticCff.Build();
        var cff = CffTable.Parse(bytes);
        Assert.False(cff.IsCidKeyed);
    }

    [Fact]
    public void Parse_throws_on_empty_input()
    {
        Assert.Throws<ArgumentException>(() => CffTable.Parse(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Parse_is_deterministic_across_repeated_calls()
    {
        var bytes = SyntheticCff.Build();
        var a = CffTable.Parse(bytes);
        var b = CffTable.Parse(bytes);

        Assert.Equal(a.NumGlyphs, b.NumGlyphs);
        Assert.Equal(a.FontName, b.FontName);
        for (var i = 0; i < a.NumGlyphs; i++)
        {
            Assert.Equal(a.GetGlyphSidOrCid(i), b.GetGlyphSidOrCid(i));
            Assert.Equal(a.GetCharStringBytes(i).ToArray(), b.GetCharStringBytes(i).ToArray());
        }
    }
}
