// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Cascade;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="CssMediaContext"/> — verifies the v1 media-query keyword
/// matcher per HTML 4 grammar (comma-separated media types) plus the CSS3 <c>only</c>
/// + <c>not</c> prefixes.
/// </summary>
public sealed class CssMediaContextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_query_always_matches(string? query)
    {
        var ctx = CssMediaContext.DefaultPrint;
        Assert.True(ctx.Matches(query));
    }

    [Fact]
    public void All_keyword_matches_any_media()
    {
        var print = CssMediaContext.DefaultPrint;
        var screen = print with { MediaType = "screen" };
        Assert.True(print.Matches("all"));
        Assert.True(screen.Matches("all"));
    }

    [Theory]
    [InlineData("print", true)]
    [InlineData("screen", false)]
    [InlineData("speech", false)]
    public void Single_media_type_matches_exact(string query, bool expected)
    {
        var print = CssMediaContext.DefaultPrint;
        Assert.Equal(expected, print.Matches(query));
    }

    [Fact]
    public void Comma_separated_list_matches_if_any_token_matches()
    {
        var print = CssMediaContext.DefaultPrint;
        Assert.True(print.Matches("screen, print, speech"));
        Assert.False(print.Matches("screen, projection"));
    }

    [Fact]
    public void Only_prefix_is_stripped()
    {
        var print = CssMediaContext.DefaultPrint;
        Assert.True(print.Matches("only print"));
        Assert.False(print.Matches("only screen"));
    }

    [Fact]
    public void Not_all_never_matches()
    {
        var print = CssMediaContext.DefaultPrint;
        Assert.False(print.Matches("not all"));
    }

    [Fact]
    public void Type_with_feature_query_matches_on_type_token()
    {
        // v1 partial: take the leading media-type token, ignore the feature expression.
        // Future: full Media Queries L4 evaluator (Phase 3 follow-up).
        var print = CssMediaContext.DefaultPrint;
        Assert.True(print.Matches("print and (min-width: 800px)"));
        Assert.False(print.Matches("screen and (min-width: 800px)"));
    }

    [Fact]
    public void DefaultPrint_has_letter_size_viewport_at_96_dpi()
    {
        var ctx = CssMediaContext.DefaultPrint;
        Assert.Equal("print", ctx.MediaType);
        Assert.Equal(816, ctx.ViewportWidthPx);   // 8.5 in × 96
        Assert.Equal(1056, ctx.ViewportHeightPx); // 11 in × 96
        Assert.Equal(1.0, ctx.DevicePixelRatio);
        Assert.Equal("light", ctx.PreferredColorScheme);
    }
}
