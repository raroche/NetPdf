// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Shaping;
using Xunit;

namespace NetPdf.UnitTests.Text.Shaping;

public sealed class ShapedGlyphTests
{
    [Fact]
    public void Constructor_assigns_every_field()
    {
        var g = new ShapedGlyph(
            GlyphId: 7,
            XAdvance: 12.5f,
            YAdvance: 0f,
            XOffset: 0.5f,
            YOffset: -0.25f,
            Cluster: 3);

        Assert.Equal((ushort)7, g.GlyphId);
        Assert.Equal(12.5f, g.XAdvance);
        Assert.Equal(0f, g.YAdvance);
        Assert.Equal(0.5f, g.XOffset);
        Assert.Equal(-0.25f, g.YOffset);
        Assert.Equal(3, g.Cluster);
    }

    [Fact]
    public void Equality_is_field_wise()
    {
        var a = new ShapedGlyph(1, 10f, 0f, 0f, 0f, 0);
        var b = new ShapedGlyph(1, 10f, 0f, 0f, 0f, 0);
        var c = new ShapedGlyph(1, 11f, 0f, 0f, 0f, 0);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
