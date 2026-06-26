// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>Phase 4 border-image (PR 4) — unit tests for <see cref="CssBorderImage_Parser"/>: shorthand +
/// longhand parsing, the slice 1–4 shorthand, the fill keyword, repeat axes, and the no-source null case.
/// A unitless slice number is stored as the negative sentinel <c>-(px + 1)</c> (resolved at paint).</summary>
public sealed class CssBorderImageParserTests
{
    [Fact]
    public void Shorthand_source_slice_and_fill()
    {
        var c = CssBorderImage_Parser.TryParse("url(border.png) 30 fill", null, null, null);
        Assert.NotNull(c);
        Assert.Equal("border.png", c!.SourceUrl);
        Assert.True(c.Fill);
        Assert.Equal(-31.0, c.SliceTopFrac, precision: 4);     // 30px → sentinel -(30+1)
        Assert.Equal(-31.0, c.SliceLeftFrac, precision: 4);    // 1-value shorthand → all four
    }

    [Fact]
    public void Longhands_override_and_percent_slices_resolve()
    {
        var c = CssBorderImage_Parser.TryParse(
            null, "url(b.png)", "10% 20% 30% 40%", "round space");
        Assert.Equal("b.png", c!.SourceUrl);
        Assert.Equal(0.10, c.SliceTopFrac, precision: 4);
        Assert.Equal(0.20, c.SliceRightFrac, precision: 4);
        Assert.Equal(0.30, c.SliceBottomFrac, precision: 4);
        Assert.Equal(0.40, c.SliceLeftFrac, precision: 4);
        Assert.Equal(BorderImageRepeat.Round, c.RepeatX);
        Assert.Equal(BorderImageRepeat.Space, c.RepeatY);
    }

    [Fact]
    public void Single_repeat_keyword_applies_to_both_axes()
    {
        var c = CssBorderImage_Parser.TryParse(null, "url(b.png)", "20%", "repeat");
        Assert.Equal(BorderImageRepeat.Repeat, c!.RepeatX);
        Assert.Equal(BorderImageRepeat.Repeat, c.RepeatY);
    }

    [Fact]
    public void No_url_source_returns_null()
    {
        Assert.Null(CssBorderImage_Parser.TryParse("none", null, null, null));
        Assert.Null(CssBorderImage_Parser.TryParse(
            "linear-gradient(red, blue) 30", null, null, null)); // gradient source not supported this cut
        Assert.Null(CssBorderImage_Parser.TryParse(null, null, null, null));
    }
}
