// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

public sealed class HtmlDefaultDisplayTests
{
    [Theory]
    [InlineData("div",        "block")]
    [InlineData("DIV",        "block")]               // case-insensitive
    [InlineData("p",          "block")]
    [InlineData("section",    "block")]
    [InlineData("article",    "block")]
    [InlineData("h1",         "block")]
    [InlineData("h6",         "block")]
    [InlineData("li",         "list-item")]
    [InlineData("ul",         "block")]
    [InlineData("ol",         "block")]
    [InlineData("table",      "table")]
    [InlineData("tbody",      "table-row-group")]
    [InlineData("thead",      "table-header-group")]
    [InlineData("tfoot",      "table-footer-group")]
    [InlineData("tr",         "table-row")]
    [InlineData("td",         "table-cell")]
    [InlineData("th",         "table-cell")]
    [InlineData("colgroup",   "table-column-group")]
    [InlineData("col",        "table-column")]
    [InlineData("caption",    "table-caption")]
    [InlineData("span",       "inline")]
    [InlineData("a",          "inline")]
    [InlineData("em",         "inline")]
    [InlineData("strong",     "inline")]
    [InlineData("img",        "inline")]
    [InlineData("video",      "inline")]
    public void Known_tag_returns_spec_default_display(string tag, string expected) =>
        Assert.Equal(expected, HtmlDefaultDisplay.GetDefault(tag));

    [Fact]
    public void Unknown_tag_falls_back_to_inline_per_CSS_spec_default()
    {
        Assert.Equal("inline", HtmlDefaultDisplay.GetDefault("custom-element"));
        Assert.Equal("inline", HtmlDefaultDisplay.GetDefault("xyz"));
    }

    [Fact]
    public void Empty_or_null_tag_falls_back_to_spec_default()
    {
        Assert.Equal("inline", HtmlDefaultDisplay.GetDefault(""));
        Assert.Equal("inline", HtmlDefaultDisplay.GetDefault(null!));
    }
}
