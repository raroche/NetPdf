// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Unit tests for <see cref="CssStringParser"/> — pinned per Task 12 hardening
/// review Rec 4 to keep cycle-1 generated content from rendering non-string
/// CSS forms (counter / attr / url / quotes) as literal text.
/// </summary>
public sealed class CssStringParserTests
{
    private static string ParseOk(string value)
    {
        Assert.True(CssStringParser.TryParseSingleString(value, out var content),
            $"Expected '{value}' to parse as a single CSS string");
        return content;
    }

    private static void AssertReject(string value) =>
        Assert.False(CssStringParser.TryParseSingleString(value, out _),
            $"Expected '{value}' to be REJECTED");

    // ============================================================
    // Accepted: single quoted string
    // ============================================================

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("'hello'", "hello")]
    [InlineData("\"\"", "")]                // empty string is legal
    [InlineData("''", "")]
    [InlineData("  \"hello\"  ", "hello")]   // tolerates leading/trailing whitespace
    [InlineData("\"a b c\"", "a b c")]
    [InlineData("\"unicode 字\"", "unicode 字")]
    public void Single_quoted_string_decodes(string input, string expected) =>
        Assert.Equal(expected, ParseOk(input));

    // ============================================================
    // Accepted: hex escapes per CSS Syntax §4.3.7
    // ============================================================

    [Theory]
    [InlineData("\"\\41\"", "A")]            // \41 → 'A'
    [InlineData("\"\\41 BC\"", "ABC")]       // trailing whitespace consumed; BC literals
    [InlineData("\"\\000041\"", "A")]        // 6-digit form
    [InlineData("\"\\A \"", "\n")]           // \A + space → newline (space consumed)
    [InlineData("\"\\9 b\"", "\tb")]         // \9 + space → tab; b literal
    public void Hex_escape_decodes(string input, string expected) =>
        Assert.Equal(expected, ParseOk(input));

    [Fact]
    public void Hex_escape_greedy_consumes_up_to_six_hex_digits()
    {
        // Per CSS Syntax 3 §4.3.7: a hex escape consumes up to 6 hex digits.
        // `\09b` = U+009B (3 hex digits — `b` is hex), NOT tab + 'b'. To get
        // tab + 'b' the author must write `\9 b` with a separator space (the
        // space is consumed; `b` becomes a literal char).
        Assert.Equal("\u009b", ParseOk("\"\\09b\""));
    }

    [Fact]
    public void Line1_then_A_escape_then_more_consumes_following_letter_as_hex()
    {
        // Following the same rule: `\Aline2` = U+0AL... wait, only first 6
        // characters that are hex digits get consumed. \Aline2 → \A + l(non-hex) →
        // U+000A + "line2". So this DOES decode to "line1\nline2".
        Assert.Equal("line1\nline2", ParseOk("\"line1\\Aline2\""));
    }

    [Theory]
    [InlineData("\"\\\"\"", "\"")]           // escaped quote
    [InlineData("'\\''", "'")]                // escaped quote in single-quoted string
    [InlineData("\"\\\\\"", "\\")]           // escaped backslash
    [InlineData("\"\\?\"", "?")]              // any other escape → literal char
    public void Generic_escape_takes_next_char_literally(string input, string expected) =>
        Assert.Equal(expected, ParseOk(input));

    [Fact]
    public void Line_continuation_consumes_backslash_plus_newline()
    {
        // "abc\<LF>def" — the line break is consumed (line-continuation form).
        var input = "\"abc\\\ndef\"";
        Assert.Equal("abcdef", ParseOk(input));
    }

    // ============================================================
    // Rejected: not a single string
    // ============================================================

    [Theory]
    [InlineData("hello")]                                // bare ident
    [InlineData("none")]
    [InlineData("normal")]
    [InlineData("counter(items)")]                       // functional
    [InlineData("attr(data-label)")]
    [InlineData("url(foo.png)")]
    [InlineData("open-quote")]
    [InlineData("close-quote")]
    [InlineData("\"PRE\" \"POST\"")]                      // multi-token concatenation
    [InlineData("\"PRE\" counter(items)")]                 // mixed
    [InlineData("\"\"123")]                                // trailing tokens
    public void Non_single_string_input_is_rejected(string input) =>
        AssertReject(input);

    [Theory]
    [InlineData("\"unterminated")]                        // missing close quote
    [InlineData("\"\\")]                                  // dangling backslash
    [InlineData("\"a\nb\"")]                              // unescaped newline inside string
    [InlineData("")]                                       // empty
    [InlineData("\"")]                                     // single quote char only
    public void Malformed_string_input_is_rejected(string input) =>
        AssertReject(input);
}
