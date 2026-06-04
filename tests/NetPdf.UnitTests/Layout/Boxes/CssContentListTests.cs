// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Direct parser tests for the Task 14 cycle-1 content-list extension —
/// multi-string concatenation + attr() resolution against a host element.
/// Tighter coverage than the BoxBuilderPseudoTests (no cascade in the loop).
/// </summary>
public sealed class CssContentListTests
{
    private static async Task<IElement> MakeHost(string html, string id)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content(html));
        return doc.QuerySelector("#" + id)!;
    }

    [Fact]
    public async Task Single_string_returns_decoded_text()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.True(CssContentList.TryParse("\"hello\"", host, out var result));
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task Two_strings_concatenate()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.True(CssContentList.TryParse("\"a\" \"b\"", host, out var result));
        Assert.Equal("ab", result);
    }

    [Fact]
    public async Task Mixed_quotes_concatenate()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.True(CssContentList.TryParse("\"a\" 'b' \"c\"", host, out var result));
        Assert.Equal("abc", result);
    }

    [Fact]
    public async Task Attr_resolves_attribute_value()
    {
        var host = await MakeHost("<p id='h' data-key='value'>x</p>", "h");
        Assert.True(CssContentList.TryParse("attr(data-key)", host, out var result));
        Assert.Equal("value", result);
    }

    [Fact]
    public async Task Attr_with_missing_attribute_returns_empty_string()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.True(CssContentList.TryParse("attr(missing)", host, out var result));
        Assert.Equal("", result);
    }

    [Fact]
    public async Task Strings_and_attr_mix()
    {
        var host = await MakeHost("<p id='h' data-x='X'>y</p>", "h");
        Assert.True(CssContentList.TryParse("\"<\" attr(data-x) \">\"", host, out var result));
        Assert.Equal("<X>", result);
    }

    [Fact]
    public async Task Counter_function_unsupported_returns_false()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse("counter(items)", host, out _));
    }

    [Fact]
    public async Task Url_function_unsupported_returns_false()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse("url(image.png)", host, out _));
    }

    [Fact]
    public async Task Open_quote_keyword_unsupported_returns_false()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse("open-quote", host, out _));
    }

    [Fact]
    public async Task Empty_input_returns_false()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse("", host, out _));
        Assert.False(CssContentList.TryParse("   ", host, out _));
    }

    [Fact]
    public async Task Attr_uppercase_function_name_works()
    {
        // CSS function names are ASCII case-insensitive.
        var host = await MakeHost("<p id='h' data-x='Y'>x</p>", "h");
        Assert.True(CssContentList.TryParse("ATTR(data-x)", host, out var result));
        Assert.Equal("Y", result);
    }

    [Fact]
    public async Task Attr_with_type_argument_is_rejected_per_Rec_4()
    {
        // Modern attr(name type) form — cycle 1 rejects rather than silently
        // dropping the type and treating as bare attr(name). Phase 2 cycle-2
        // will implement the typed form properly.
        var host = await MakeHost("<p id='h' data-x='real'>x</p>", "h");
        Assert.False(CssContentList.TryParse("attr(data-x string)", host, out _));
    }

    [Fact]
    public async Task Attr_with_fallback_argument_is_rejected_per_Rec_4()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse("attr(missing, 'fallback')", host, out _));
    }

    [Fact]
    public async Task Attr_with_type_and_fallback_is_rejected_per_Rec_4()
    {
        var host = await MakeHost("<p id='h' data-x='real'>x</p>", "h");
        Assert.False(CssContentList.TryParse("attr(data-x string, 'fallback')", host, out _));
    }

    [Fact]
    public async Task Attr_with_only_whitespace_inside_after_name_still_works()
    {
        // A single trailing space before the close-paren is fine.
        var host = await MakeHost("<p id='h' data-x='Y'>x</p>", "h");
        Assert.True(CssContentList.TryParse("attr(data-x )", host, out var result));
        Assert.Equal("Y", result);
    }

    [Fact]
    public async Task Escape_in_string_decoded_properly()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        // \41 → 'A' per CSS Syntax §4.3.7.
        Assert.True(CssContentList.TryParse("\"\\41 BC\"", host, out var result));
        Assert.Equal("ABC", result);
    }

    // ---- page counters (Task 21 cycle 9) ----

    [Theory]
    [InlineData("counter(page)", "3")]
    [InlineData("counter(pages)", "7")]
    [InlineData("counter(page, decimal)", "3")]   // the default style, explicit
    [InlineData("counter( page )", "3")]          // whitespace tolerance
    [InlineData("\"Page \" counter(page)", "Page 3")]   // string + counter mix
    [InlineData("counter(page) \" of \" counter(pages)", "3 of 7")]
    public async Task TryParse_resolves_page_counters(string raw, string expected)
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.True(CssContentList.TryParse(raw, host, new CssContentList.PageCounters(3, 7), out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("counter(chapter)")]               // a non-page counter name
    [InlineData("counter(page, lower-roman)")]      // an unsupported counter style
    [InlineData("counters(page, \".\")")]           // counters() (plural) is unsupported
    public async Task TryParse_rejects_unsupported_counters(string raw)
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse(raw, host, new CssContentList.PageCounters(3, 7), out _));
    }

    [Fact]
    public async Task TryParse_counter_page_is_unsupported_without_a_page_context()
    {
        // No PageCounters supplied (e.g. body / pseudo-element content) → counter(page) is unsupported.
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.False(CssContentList.TryParse("counter(page)", host, out _));
    }
}
