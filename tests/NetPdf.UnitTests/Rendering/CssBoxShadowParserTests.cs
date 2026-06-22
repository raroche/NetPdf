// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 shadows — unit tests for <see cref="CssBoxShadow_Parser"/>: the
/// <c>[ inset? &amp;&amp; &lt;length&gt;{2,4} &amp;&amp; &lt;color&gt;? ]#</c> grammar, units,
/// multi-layer lists, and rejection of unsupported forms.</summary>
public sealed class CssBoxShadowParserTests
{
    [Fact]
    public void Two_lengths_are_offsets_with_zero_blur_and_spread()
    {
        var s = Assert.Single(CssBoxShadow_Parser.TryParse("2px 4px")!);
        Assert.False(s.Inset);
        Assert.Equal(2.0, s.OffsetXPx, 4);
        Assert.Equal(4.0, s.OffsetYPx, 4);
        Assert.Equal(0.0, s.BlurPx, 4);
        Assert.Equal(0.0, s.SpreadPx, 4);
        Assert.Null(s.ColorRaw);
    }

    [Fact]
    public void Three_and_four_lengths_are_blur_then_spread()
    {
        var three = Assert.Single(CssBoxShadow_Parser.TryParse("1px 2px 6px")!);
        Assert.Equal(6.0, three.BlurPx, 4);
        Assert.Equal(0.0, three.SpreadPx, 4);

        var four = Assert.Single(CssBoxShadow_Parser.TryParse("1px 2px 6px 8px")!);
        Assert.Equal(6.0, four.BlurPx, 4);
        Assert.Equal(8.0, four.SpreadPx, 4);
    }

    [Fact]
    public void Inset_and_color_parse_in_any_order()
    {
        var a = Assert.Single(CssBoxShadow_Parser.TryParse("inset 2px 4px red")!);
        Assert.True(a.Inset);
        Assert.Equal("red", a.ColorRaw);

        var b = Assert.Single(CssBoxShadow_Parser.TryParse("red 2px 4px inset")!);
        Assert.True(b.Inset);
        Assert.Equal("red", b.ColorRaw);
        Assert.Equal(2.0, b.OffsetXPx, 4);
    }

    [Fact]
    public void Function_color_with_internal_commas_stays_one_token()
    {
        var s = Assert.Single(CssBoxShadow_Parser.TryParse("2px 2px 5px rgba(0, 0, 0, 0.5)")!);
        Assert.Equal("rgba(0, 0, 0, 0.5)", s.ColorRaw);
        Assert.Equal(5.0, s.BlurPx, 4);
    }

    [Fact]
    public void Comma_separated_layers_each_parse()
    {
        var layers = CssBoxShadow_Parser.TryParse("1px 1px black, 2px 2px 3px red");
        Assert.NotNull(layers);
        Assert.Equal(2, layers!.Count);
        Assert.Equal("black", layers[0].ColorRaw);
        Assert.Equal("red", layers[1].ColorRaw);
        Assert.Equal(3.0, layers[1].BlurPx, 4);
    }

    [Fact]
    public void Negative_offsets_are_allowed_absolute_units_convert_to_px()
    {
        var s = Assert.Single(CssBoxShadow_Parser.TryParse("-0.05in 0 0 black")!);
        Assert.Equal(-4.8, s.OffsetXPx, 4); // 0.05in × 96
        Assert.Equal(0.0, s.OffsetYPx, 4);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("red")]            // no lengths
    [InlineData("2px")]            // only one length
    [InlineData("2em 4px")]        // font-relative unit not resolved here
    [InlineData("2px 4px 50%")]    // percentage blur not a length we resolve
    [InlineData("2px 2px -3px red")] // negative blur is invalid
    public void Unsupported_or_empty_forms_return_null(string value)
    {
        Assert.Null(CssBoxShadow_Parser.TryParse(value));
    }
}
