// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

public sealed class PostTableTests
{
    [Fact]
    public void Parse_v30_decodes_underline_and_isFixedPitch()
    {
        var post = PostTable.Parse(SyntheticFont.PostBytes());
        Assert.Equal(PostTable.Version30, post.Version);
        Assert.Equal((short)-100, post.UnderlinePosition);
        Assert.Equal((short)50, post.UnderlineThickness);
        Assert.False(post.IsMonospaced);
    }

    [Fact]
    public void Parse_marks_monospaced_font_when_isFixedPitch_nonzero()
    {
        var bytes = SyntheticFont.PostBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), 1);
        var post = PostTable.Parse(bytes);
        Assert.True(post.IsMonospaced);
    }

    [Fact]
    public void Parse_throws_on_unknown_version()
    {
        var bytes = SyntheticFont.PostBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFEBABEu);
        Assert.Throws<InvalidDataException>(() => PostTable.Parse(bytes));
    }
}
