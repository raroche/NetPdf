// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
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
    [InlineData("counter(page, lower-roman)", "iii")]          // Task 21 — counter styles
    [InlineData("counter(page, upper-roman)", "III")]
    [InlineData("counter(page, lower-alpha)", "c")]           // 3rd letter
    [InlineData("counter(page, upper-latin)", "C")]          // -latin alias of -alpha
    [InlineData("counter(page, decimal-leading-zero)", "03")]
    [InlineData("counter(pages, lower-roman)", "vii")]       // the total, roman
    [InlineData("\"Page \" counter(page, upper-roman) \" of \" counter(pages, upper-roman)", "Page III of VII")]
    [InlineData("counter(page, hebrew)", "3")]               // an unimplemented style → decimal (review P2)
    [InlineData("counter(page, cjk-ideographic)", "3")]
    [InlineData("counter(page, not-a-style)", "3")]          // an undefined name → decimal
    public async Task TryParse_resolves_page_counters(string raw, string expected)
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        Assert.True(CssContentList.TryParse(raw, host, new CssContentList.PageCounters(3, 7), out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("counter(chapter)")]               // a non-page counter name
    [InlineData("counter(page, )")]                 // empty style after the comma → malformed
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

    [Theory]
    [InlineData(0, 1)]   // page < 1
    [InlineData(1, 0)]   // pages < 1
    [InlineData(2, 1)]   // page > pages
    public void PageCounters_rejects_out_of_range_values(int page, int pages)
    {
        // A contract guard before the multi-page driver passes dynamic values (review P3).
        Assert.Throws<ArgumentOutOfRangeException>(() => new CssContentList.PageCounters(page, pages));
    }

    [Fact]
    public void PageCounters_accepts_a_valid_page_of_total()
    {
        var pc = new CssContentList.PageCounters(3, 7);
        Assert.Equal(3, pc.Page);
        Assert.Equal(7, pc.Pages);
    }

    // ---- string(name) (Task 22) + element(name) (Task 23) ----

    private static CssContentList.MarginContentContext Ctx(
        (string Key, string Value)[]? named = null, (string Key, string Value)[]? running = null,
        (string Key, string Value)[]? namedFirst = null, (string Key, string Value)[]? runningFirst = null)
    {
        Dictionary<string, string>? n = null, r = null, nf = null, rf = null;
        if (named is not null) { n = new(); foreach (var (k, v) in named) n[k] = v; }
        if (running is not null) { r = new(); foreach (var (k, v) in running) r[k] = v; }
        // Mirror the collector: a single assignment / element sets BOTH first + last. When a test supplies
        // only `named` / `running` (a single-occurrence page), default the *First map to it so the GCPM
        // `first` default resolves for string() AND element().
        if (namedFirst is not null) { nf = new(); foreach (var (k, v) in namedFirst) nf[k] = v; } else nf = n;
        if (runningFirst is not null) { rf = new(); foreach (var (k, v) in runningFirst) rf[k] = v; } else rf = r;
        return new CssContentList.MarginContentContext(n, r, nf, rf);
    }

    [Fact]
    public async Task String_function_first_and_last_keywords_pick_the_assignment()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(named: new[] { ("t", "last") }, namedFirst: new[] { ("t", "first") });
        Assert.True(CssContentList.TryParse("string(t, first)", host, new CssContentList.PageCounters(1, 1), ctx, out var f));
        Assert.Equal("first", f);
        Assert.True(CssContentList.TryParse("string(t, last)", host, new CssContentList.PageCounters(1, 1), ctx, out var l));
        Assert.Equal("last", l);
        // No keyword → the GCPM DEFAULT is `first` (the first assignment on the page), NOT last (review P1).
        Assert.True(CssContentList.TryParse("string(t)", host, new CssContentList.PageCounters(1, 1), ctx, out var d));
        Assert.Equal("first", d);
    }

    [Theory]
    [InlineData("string(t, start)")]         // cross-page → deferred (needs the multi-page driver)
    [InlineData("string(t, first-except)")]  // niche GCPM keyword → deferred
    [InlineData("string(t, bogus)")]         // unknown keyword
    public async Task String_function_unsupported_position_keyword_bails(string raw)
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(named: new[] { ("t", "v") }, namedFirst: new[] { ("t", "v") });
        Assert.False(CssContentList.TryParse(raw, host, new CssContentList.PageCounters(1, 1), ctx, out _));
    }

    [Fact]
    public async Task String_function_resolves_a_named_string()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(named: new[] { ("t", "hello") });
        Assert.True(CssContentList.TryParse("string(t)", host, new CssContentList.PageCounters(1, 1), ctx, out var result));
        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task String_function_with_an_undefined_name_is_empty()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(named: new[] { ("t", "hello") });
        Assert.True(CssContentList.TryParse("string(missing)", host, new CssContentList.PageCounters(1, 1), ctx, out var result));
        Assert.Equal("", result);   // undefined named string → empty (CSS GCPM L3)
    }

    [Fact]
    public async Task String_function_mixed_with_a_literal_concatenates()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(named: new[] { ("t", "World") });
        Assert.True(CssContentList.TryParse("\"Hello \" string(t)", host, new CssContentList.PageCounters(1, 1), ctx, out var result));
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public async Task Element_function_resolves_running_element_text()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(running: new[] { ("rh", "Chapter") });
        Assert.True(CssContentList.TryParse("element(rh)", host, new CssContentList.PageCounters(1, 1), ctx, out var result));
        Assert.Equal("Chapter", result);
    }

    [Fact]
    public async Task Element_function_first_and_last_keywords_pick_the_occurrence()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(running: new[] { ("rh", "last") }, runningFirst: new[] { ("rh", "first") });
        Assert.True(CssContentList.TryParse("element(rh, first)", host, new CssContentList.PageCounters(1, 1), ctx, out var f));
        Assert.Equal("first", f);
        Assert.True(CssContentList.TryParse("element(rh, last)", host, new CssContentList.PageCounters(1, 1), ctx, out var l));
        Assert.Equal("last", l);
        // No keyword → the GCPM DEFAULT is `first` (the first running element on the page), like string().
        Assert.True(CssContentList.TryParse("element(rh)", host, new CssContentList.PageCounters(1, 1), ctx, out var d));
        Assert.Equal("first", d);
    }

    [Theory]
    [InlineData("element(rh, start)")]         // cross-page → deferred
    [InlineData("element(rh, first-except)")]  // niche GCPM keyword → deferred
    [InlineData("element(rh, bogus)")]         // unknown keyword
    public async Task Element_function_unsupported_position_keyword_bails(string raw)
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        var ctx = Ctx(running: new[] { ("rh", "v") }, runningFirst: new[] { ("rh", "v") });
        Assert.False(CssContentList.TryParse(raw, host, new CssContentList.PageCounters(1, 1), ctx, out _));
    }

    [Fact]
    public async Task String_function_without_a_margin_context_is_unsupported()
    {
        var host = await MakeHost("<p id='h'>x</p>", "h");
        // The body / pseudo-element path has no margin context → string() is unsupported there (it is
        // only valid in page-margin boxes), so the parse fails (the pseudo generates no box).
        Assert.False(CssContentList.TryParse("string(t)", host, out _));
    }

    // ---- content() in string-set (Task 22 — the content() form) ----

    [Theory]
    [InlineData("content()")]        // bare form → the element's text
    [InlineData("content(text)")]    // explicit text target → the element's text
    public async Task StringSet_content_function_resolves_the_host_text(string raw)
    {
        var host = await MakeHost("<h1 id='h'>Chapter One</h1>", "h");
        Assert.True(CssContentList.TryParseStringSet(raw, host, out var result));
        Assert.Equal("Chapter One", result);
    }

    [Fact]
    public async Task StringSet_content_mixed_with_a_literal_concatenates()
    {
        var host = await MakeHost("<h1 id='h'>One</h1>", "h");
        Assert.True(CssContentList.TryParseStringSet("\"Ch. \" content()", host, out var result));
        Assert.Equal("Ch. One", result);
    }

    [Fact]
    public async Task StringSet_content_collapses_source_whitespace_per_gcpm()
    {
        // CSS GCPM §3.1: content() takes the element string as if white-space: normal — so a formatted,
        // INDENTED heading with a NESTED element collapses to single-spaced text (no leading/trailing
        // whitespace, no embedded newlines/indentation, runs of spaces → one). Else the raw source
        // indentation would leak into the running header.
        var host = await MakeHost("<h1 id='h'>\n  Chapter   <span>One</span>\n</h1>", "h");
        Assert.True(CssContentList.TryParseStringSet("content()", host, out var result));
        Assert.Equal("Chapter One", result);
    }

    [Fact]
    public async Task Content_function_is_unsupported_outside_a_string_set()
    {
        var host = await MakeHost("<h1 id='h'>One</h1>", "h");
        // content() is a GCPM string-set value only — the regular (margin-box / pseudo) content path must
        // NOT resolve it, so the parse fails there (it stays a tracked unsupported token).
        Assert.False(CssContentList.TryParse("content()", host, out _));
    }

    [Theory]
    [InlineData("content(first-letter)")] // typographic targets stay deferred
    [InlineData("content(marker)")]
    [InlineData("content(text, more)")]  // a second argument is malformed for the first cut
    public async Task StringSet_content_with_an_unsupported_target_is_rejected(string raw)
    {
        var host = await MakeHost("<h1 id='h'>One</h1>", "h");
        Assert.False(CssContentList.TryParseStringSet(raw, host, out _));
    }

    // ---- content(before|after) (content-pseudo cycle) ----

    [Theory]
    [InlineData("content(before)", "Ch. ")]                  // the pseudo's content alone
    [InlineData("content(before) content()", "Ch. One")]     // composing with the element text
    [InlineData("CONTENT(BEFORE)", "Ch. ")]                  // case-insensitive
    [InlineData("content( before )", "Ch. ")]                // whitespace-tolerant
    public async Task StringSet_content_before_resolves_via_the_pseudo_resolver(string raw, string expected)
    {
        var host = await MakeHost("<h1 id='h'>One</h1>", "h");
        Assert.True(CssContentList.TryParseStringSet(
            raw, host, pseudoName => pseudoName == "before" ? "\"Ch. \"" : null, out var result), raw);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task StringSet_content_before_without_a_resolver_resolves_empty()
    {
        // The legacy overload (null resolver) — content(before) contributes the empty string but
        // the assignment SUCCEEDS (GCPM: an absent pseudo is empty), it is no longer a reject.
        var host = await MakeHost("<h1 id='h'>One</h1>", "h");
        Assert.True(CssContentList.TryParseStringSet("content(before) content()", host, out var result));
        Assert.Equal("One", result);
    }
}
