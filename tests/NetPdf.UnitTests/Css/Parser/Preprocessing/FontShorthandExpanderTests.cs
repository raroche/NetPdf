// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Unit tests for <see cref="FontShorthandExpander"/> — Phase 3 Task 21 cycle 6, expanding the
/// <c>font</c> shorthand for <c>@page</c> margin-box bodies into the longhands NetPdf consumes.
/// </summary>
public sealed class FontShorthandExpanderTests
{
    [Theory]
    // value, expected style, weight, size, family
    [InlineData("12pt serif", "normal", "normal", "12pt", "serif")]
    [InlineData("bold 12pt serif", "normal", "bold", "12pt", "serif")]
    [InlineData("italic bold 10pt Georgia", "italic", "bold", "10pt", "Georgia")]
    [InlineData("700 1.2em Arial", "normal", "700", "1.2em", "Arial")]      // bare number = weight
    [InlineData("medium serif", "normal", "normal", "medium", "serif")]      // size keyword
    [InlineData("italic 9pt/1.4 serif", "italic", "normal", "9pt", "serif")] // line-height (attached) consumed
    [InlineData("12pt / 1.5 serif", "normal", "normal", "12pt", "serif")]    // line-height (spaced) consumed
    [InlineData("small-caps 11px monospace", "normal", "normal", "11px", "monospace")] // variant consumed
    public void TryExpand_parses_valid_forms(
        string value, string style, string weight, string size, string family)
    {
        Assert.True(FontShorthandExpander.TryExpand(value, out var s, out var w, out var sz, out var f));
        Assert.Equal(style, s);
        Assert.Equal(weight, w);
        Assert.Equal(size, sz);
        Assert.Equal(family, f);
    }

    [Fact]
    public void TryExpand_preserves_a_multi_word_quoted_family_list()
    {
        Assert.True(FontShorthandExpander.TryExpand(
            "italic 9pt \"Times New Roman\", serif", out var s, out _, out var sz, out var f));
        Assert.Equal("italic", s);
        Assert.Equal("9pt", sz);
        Assert.Equal("\"Times New Roman\", serif", f);
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("initial")]
    [InlineData("unset")]
    public void TryExpand_css_wide_keyword_applies_to_every_longhand(string keyword)
    {
        Assert.True(FontShorthandExpander.TryExpand(keyword, out var s, out var w, out var sz, out var f));
        Assert.Equal(keyword, s);
        Assert.Equal(keyword, w);
        Assert.Equal(keyword, sz);
        Assert.Equal(keyword, f);
    }

    [Theory]
    [InlineData("caption")]      // a system-font keyword — not modeled
    [InlineData("status-bar")]
    [InlineData("bold")]         // no size + no family
    [InlineData("12pt")]         // no family
    [InlineData("bold serif")]   // no font-size token (serif is a family)
    [InlineData("")]
    public void TryExpand_rejects_system_keywords_and_malformed(string value)
    {
        Assert.False(FontShorthandExpander.TryExpand(value, out _, out _, out _, out _));
    }
}
