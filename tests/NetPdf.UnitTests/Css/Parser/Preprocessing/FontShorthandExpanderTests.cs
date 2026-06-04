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
    [InlineData("12pt / 1.5 serif", "normal", "normal", "12pt", "serif")]    // line-height (lone slash) consumed
    [InlineData("12pt/ 1.4 serif", "normal", "normal", "12pt", "serif")]     // slash trails size, lh is next token (review P2)
    [InlineData("12pt /1.5 serif", "normal", "normal", "12pt", "serif")]     // slash+lh detached from size (review P2)
    [InlineData("small-caps 11px monospace", "normal", "normal", "11px", "monospace")] // variant consumed
    [InlineData("0 serif", "normal", "normal", "0", "serif")]                // unitless zero is a valid <length> (Copilot)
    [InlineData("italic/*c*/12pt serif", "italic", "normal", "12pt", "serif")] // CSS comment is whitespace (Copilot)
    // Relative weight/size survive expansion as DEFERRED longhands — PR135 (shorthand) + PR136
    // (deferred parent-relative resolution) compose (the dispatch validates `bolder`/`1.5em` as
    // Deferred, not Invalid, so atomic validation accepts them).
    [InlineData("bolder 1.5em serif", "normal", "bolder", "1.5em", "serif")]
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
    // Atomic validation (review P1): a bogus part rejects the WHOLE shorthand — no partial style.
    [InlineData("italic 12bananas serif")]    // bogus size unit
    [InlineData("12px 700 serif")]            // weight after size → invalid family
    [InlineData("1e3 serif")]                 // exponent-only is not a <font-size>
    // Duplicate leading category (each of style/variant/weight/stretch appears at most once).
    [InlineData("italic oblique 12pt serif")] // two <font-style> tokens
    [InlineData("bold 700 12pt serif")]       // two <font-weight> tokens
    // A "/" REQUIRES a <line-height> token (review P2 / Copilot).
    [InlineData("12pt/ serif")]
    [InlineData("12pt / serif")]
    // Deliberate approximation (review P4): the CSS Fonts 4 `oblique <angle>` form + an explicit
    // <font-stretch> aren't surfaced by the margin-box subset → the shorthand is rejected atomically
    // rather than silently mangled.
    [InlineData("oblique 10deg 12pt serif")]
    [InlineData("condensed oblique 25deg 753 12pt \"Helvetica Neue\", serif")]
    public void TryExpand_rejects_system_keywords_and_malformed(string value)
    {
        Assert.False(FontShorthandExpander.TryExpand(value, out _, out _, out _, out _));
    }

    [Fact]
    public void TryExpand_strips_comments_but_not_inside_quoted_family_names()
    {
        // Quote-aware comment stripping: a `/*` inside a quoted family name is NOT a comment.
        Assert.True(FontShorthandExpander.TryExpand("12pt \"A/*B\"", out var s, out _, out var sz, out var f));
        Assert.Equal("normal", s);
        Assert.Equal("12pt", sz);
        Assert.Equal("\"A/*B\"", f);
    }
}
