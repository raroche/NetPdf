// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class OpenTypeTagsTests
{
    [Theory]
    [InlineData("head", OpenTypeTags.Head)]
    [InlineData("hhea", OpenTypeTags.Hhea)]
    [InlineData("hmtx", OpenTypeTags.Hmtx)]
    [InlineData("maxp", OpenTypeTags.Maxp)]
    [InlineData("name", OpenTypeTags.Name)]
    [InlineData("OS/2", OpenTypeTags.Os2)]
    [InlineData("post", OpenTypeTags.Post)]
    [InlineData("cmap", OpenTypeTags.Cmap)]
    [InlineData("loca", OpenTypeTags.Loca)]
    [InlineData("glyf", OpenTypeTags.Glyf)]
    [InlineData("CFF ", OpenTypeTags.Cff)]
    public void Tag_constants_match_their_ascii_encoding(string ascii, uint constant)
    {
        var encoded = OpenTypeTags.FromAscii(Encoding.ASCII.GetBytes(ascii));
        Assert.Equal(constant, encoded);
    }

    [Fact]
    public void FromAscii_throws_when_input_is_not_four_bytes()
    {
        Assert.Throws<ArgumentException>(() => OpenTypeTags.FromAscii([0x68, 0x65, 0x61])); // 3 bytes
    }

    [Fact]
    public void ToAsciiString_round_trips_known_tags()
    {
        Assert.Equal("head", OpenTypeTags.ToAsciiString(OpenTypeTags.Head));
        Assert.Equal("OS/2", OpenTypeTags.ToAsciiString(OpenTypeTags.Os2));
    }

    [Fact]
    public void Sfnt_version_constants_match_spec()
    {
        Assert.Equal(0x00010000u, OpenTypeTags.SfntVersionTtf);
        Assert.Equal(OpenTypeTags.SfntVersionOtf, OpenTypeTags.FromAscii(Encoding.ASCII.GetBytes("OTTO")));
        Assert.Equal(OpenTypeTags.SfntVersionAppleTrue, OpenTypeTags.FromAscii(Encoding.ASCII.GetBytes("true")));
    }
}
