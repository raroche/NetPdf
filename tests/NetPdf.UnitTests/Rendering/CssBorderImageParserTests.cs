// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 border-image (PR 4) — unit tests for the LONGHAND parser
/// <see cref="CssBorderImage_Parser"/> + the shorthand expander
/// <see cref="BorderImageShorthandExpander"/>. The `border-image` shorthand is expanded into longhands by
/// the preprocessor so the cascade resolves shorthand-vs-longhand by source order (PR-229 review).</summary>
public sealed class CssBorderImageParserTests
{
    // ---- longhand parser ----

    [Fact]
    public void Source_slice_and_fill()
    {
        var c = CssBorderImage_Parser.TryParse("url(b.png)", "30 fill", "stretch");
        Assert.NotNull(c);
        Assert.Equal("b.png", c!.SourceUrl);
        Assert.True(c.Fill);
        Assert.Equal(-31.0, c.SliceTopFrac, precision: 4);  // 30px → sentinel -(30+1)
        Assert.Equal(-31.0, c.SliceLeftFrac, precision: 4); // 1-value shorthand → all four
    }

    [Fact]
    public void Percent_slices_and_repeat_axes()
    {
        var c = CssBorderImage_Parser.TryParse("url(b.png)", "10% 20% 30% 40%", "round space");
        Assert.Equal(0.10, c!.SliceTopFrac, precision: 4);
        Assert.Equal(0.20, c.SliceRightFrac, precision: 4);
        Assert.Equal(0.30, c.SliceBottomFrac, precision: 4);
        Assert.Equal(0.40, c.SliceLeftFrac, precision: 4);
        Assert.Equal(BorderImageRepeat.Round, c.RepeatX);
        Assert.Equal(BorderImageRepeat.Space, c.RepeatY);
    }

    [Fact]
    public void No_url_source_returns_null()
    {
        Assert.Null(CssBorderImage_Parser.TryParse("none", null, null));
        Assert.Null(CssBorderImage_Parser.TryParse("linear-gradient(red, blue)", "30", null));
        Assert.Null(CssBorderImage_Parser.TryParse(null, null, null));
    }

    [Fact]
    public void Unsupported_source_detects_gradient_but_not_none_or_url()
    {
        Assert.True(CssBorderImage_Parser.IsUnsupportedSource("linear-gradient(red, blue)"));
        Assert.False(CssBorderImage_Parser.IsUnsupportedSource("none"));
        Assert.False(CssBorderImage_Parser.IsUnsupportedSource("url(b.png)"));
        Assert.False(CssBorderImage_Parser.IsUnsupportedSource(null));
    }

    // ---- shorthand expander (cascade-order fix) ----

    [Fact]
    public void Shorthand_expands_into_five_longhands()
    {
        Assert.True(BorderImageShorthandExpander.TryExpand(
            "url(b.png) 30 fill / 10px / 5px round",
            out var source, out var slice, out var width, out var outset, out var repeat));
        Assert.Equal("url(b.png)", source);
        Assert.Contains("30", slice);
        Assert.Contains("fill", slice);
        Assert.Equal("10px", width);
        Assert.Equal("5px", outset);
        Assert.Equal("round", repeat);
    }

    [Fact]
    public void Shorthand_defaults_unset_longhands_to_their_initials()
    {
        Assert.True(BorderImageShorthandExpander.TryExpand(
            "url(b.png) 30", out var source, out var slice, out var width, out var outset, out var repeat));
        Assert.Equal("url(b.png)", source);
        Assert.Equal("30", slice);
        Assert.Equal("1", width);       // initial
        Assert.Equal("0", outset);      // initial
        Assert.Equal("stretch", repeat); // initial
    }
}
