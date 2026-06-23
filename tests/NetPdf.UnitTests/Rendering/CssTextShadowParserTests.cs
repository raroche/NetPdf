// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 shadows — unit tests for <see cref="CssTextShadow_Parser"/>: the
/// <c>[ &lt;color&gt;? &amp;&amp; &lt;length&gt;{2,3} ]#</c> grammar (no inset / spread) + rejection.</summary>
public sealed class CssTextShadowParserTests
{
    [Fact]
    public void Two_lengths_are_offsets_with_zero_blur()
    {
        var s = Assert.Single(CssTextShadow_Parser.TryParse("3px 4px")!);
        Assert.Equal(3.0, s.OffsetXPx, 4);
        Assert.Equal(4.0, s.OffsetYPx, 4);
        Assert.Equal(0.0, s.BlurPx, 4);
        Assert.Null(s.ColorRaw);
    }

    [Fact]
    public void Color_and_blur_parse_in_any_order()
    {
        var a = Assert.Single(CssTextShadow_Parser.TryParse("1px 2px 3px red")!);
        Assert.Equal(3.0, a.BlurPx, 4);
        Assert.Equal("red", a.ColorRaw);

        var b = Assert.Single(CssTextShadow_Parser.TryParse("red 1px 2px")!);
        Assert.Equal("red", b.ColorRaw);
        Assert.Equal(1.0, b.OffsetXPx, 4);
    }

    [Fact]
    public void Comma_separated_layers_each_parse()
    {
        var layers = CssTextShadow_Parser.TryParse("1px 1px #000, 0 0 4px rgba(0, 0, 255, 0.6)");
        Assert.NotNull(layers);
        Assert.Equal(2, layers!.Count);
        Assert.Equal("#000", layers[0].ColorRaw);
        Assert.Equal("rgba(0, 0, 255, 0.6)", layers[1].ColorRaw);
        Assert.Equal(4.0, layers[1].BlurPx, 4);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("red")]              // no lengths
    [InlineData("2px")]              // one length
    [InlineData("1px 2px 3px 4px")]  // text-shadow has no spread (4 lengths invalid)
    [InlineData("2em 2px")]          // unsupported unit
    [InlineData("1px 1px -2px red")] // negative blur
    [InlineData("1e400px 2px red")]  // non-finite (overflow) — PR #210 review [P2]
    public void Unsupported_or_empty_forms_return_null(string value)
    {
        Assert.Null(CssTextShadow_Parser.TryParse(value));
    }

    [Fact]
    public void Unitless_zero_offsets_parse()
    {
        var s = Assert.Single(CssTextShadow_Parser.TryParse("0 0.0 blue")!); // PR #210 review [P3]
        Assert.Equal(0.0, s.OffsetXPx, 4);
        Assert.Equal(0.0, s.OffsetYPx, 4);
    }
}
