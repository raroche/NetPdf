// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Per-method unit tests for <see cref="CssTokenizer"/>. Because the type is a
/// <c>ref struct</c>, every test instantiates a fresh tokenizer over a span and exercises
/// one operation at a time. Position tracking, balanced-block reading, and string/comment
/// skipping are the load-bearing operations the preprocessor depends on.
/// </summary>
public sealed class CssTokenizerTests
{
    [Fact]
    public void IsEnd_is_true_for_empty_input()
    {
        var tok = new CssTokenizer("".AsSpan(), null);
        Assert.True(tok.IsEnd);
    }

    [Fact]
    public void Position_starts_at_one_one()
    {
        var tok = new CssTokenizer("a".AsSpan(), "src.css");
        var loc = tok.CurrentLocation;
        Assert.Equal(1, loc.Line);
        Assert.Equal(1, loc.Column);
        Assert.Equal("src.css", loc.Source);
    }

    [Fact]
    public void ReadChar_advances_column()
    {
        var tok = new CssTokenizer("abc".AsSpan(), null);
        Assert.Equal('a', tok.ReadChar());
        Assert.Equal(2, tok.CurrentLocation.Column);
        Assert.Equal('b', tok.ReadChar());
        Assert.Equal(3, tok.CurrentLocation.Column);
    }

    [Fact]
    public void ReadChar_advances_line_on_newline()
    {
        var tok = new CssTokenizer("a\nb".AsSpan(), null);
        tok.ReadChar();
        tok.ReadChar();
        var loc = tok.CurrentLocation;
        Assert.Equal(2, loc.Line);
        Assert.Equal(1, loc.Column);
    }

    [Fact]
    public void SkipWhitespaceAndComments_skips_spaces_tabs_newlines_and_block_comments()
    {
        var tok = new CssTokenizer("  /* hi */ \t\n .foo".AsSpan(), null);
        tok.SkipWhitespaceAndComments();
        Assert.Equal('.', tok.PeekChar());
        Assert.Equal(2, tok.CurrentLocation.Line);
    }

    [Fact]
    public void SkipWhitespaceAndComments_handles_unterminated_comment_gracefully()
    {
        var tok = new CssTokenizer("/* unterminated".AsSpan(), null);
        tok.SkipWhitespaceAndComments();
        Assert.True(tok.IsEnd);
    }

    [Fact]
    public void ReadIdentifier_reads_simple_name()
    {
        var tok = new CssTokenizer("color: red".AsSpan(), null);
        var ident = tok.ReadIdentifier();
        Assert.Equal("color", ident.ToString());
        Assert.Equal(':', tok.PeekChar());
    }

    [Fact]
    public void ReadIdentifier_reads_dashed_and_underscored_names()
    {
        var tok = new CssTokenizer("font-family-foo_bar baz".AsSpan(), null);
        var ident = tok.ReadIdentifier();
        Assert.Equal("font-family-foo_bar", ident.ToString());
    }

    [Fact]
    public void ReadIdentifier_returns_empty_for_non_identifier_start()
    {
        var tok = new CssTokenizer("123abc".AsSpan(), null);
        var ident = tok.ReadIdentifier();
        Assert.True(ident.IsEmpty);
    }

    [Fact]
    public void ReadAtKeyword_reads_at_keyword_without_at_sign()
    {
        var tok = new CssTokenizer("@media print".AsSpan(), null);
        var kw = tok.ReadAtKeyword();
        Assert.Equal("media", kw.ToString());
    }

    [Fact]
    public void ReadAtKeyword_returns_empty_when_not_on_at_sign()
    {
        var tok = new CssTokenizer("foo".AsSpan(), null);
        var kw = tok.ReadAtKeyword();
        Assert.True(kw.IsEmpty);
    }

    [Fact]
    public void ReadParenthesizedBlock_reads_balanced_simple()
    {
        var tok = new CssTokenizer("(foo bar) rest".AsSpan(), null);
        var block = tok.ReadParenthesizedBlock();
        Assert.Equal("(foo bar)", block.ToString());
        Assert.Equal(' ', tok.PeekChar());
    }

    [Fact]
    public void ReadParenthesizedBlock_handles_nested_parens()
    {
        var tok = new CssTokenizer("(a (b) c) tail".AsSpan(), null);
        var block = tok.ReadParenthesizedBlock();
        Assert.Equal("(a (b) c)", block.ToString());
    }

    [Fact]
    public void ReadParenthesizedBlock_skips_strings_with_unbalanced_parens()
    {
        // The string contains a ')' that should NOT close the outer paren.
        var tok = new CssTokenizer("(\"a)b\") tail".AsSpan(), null);
        var block = tok.ReadParenthesizedBlock();
        Assert.Equal("(\"a)b\")", block.ToString());
    }

    [Fact]
    public void ReadCurlyBlock_reads_balanced_simple()
    {
        var tok = new CssTokenizer("{ color: red } rest".AsSpan(), null);
        var block = tok.ReadCurlyBlock();
        Assert.Equal("{ color: red }", block.ToString());
    }

    [Fact]
    public void ReadCurlyBlock_handles_nested_curlies()
    {
        var tok = new CssTokenizer("{ outer { inner } rest } tail".AsSpan(), null);
        var block = tok.ReadCurlyBlock();
        Assert.Equal("{ outer { inner } rest }", block.ToString());
    }

    [Fact]
    public void ReadCurlyBlock_skips_strings_with_unbalanced_curlies()
    {
        var tok = new CssTokenizer("{ \"}\" } tail".AsSpan(), null);
        var block = tok.ReadCurlyBlock();
        Assert.Equal("{ \"}\" }", block.ToString());
    }

    [Fact]
    public void ReadUntilAnyTopLevel_stops_at_delimiter()
    {
        var tok = new CssTokenizer("hello;world".AsSpan(), null);
        var read = tok.ReadUntilAnyTopLevel(";");
        Assert.Equal("hello", read.ToString());
        Assert.Equal(';', tok.PeekChar());
    }

    [Fact]
    public void ReadUntilAnyTopLevel_does_not_stop_at_delimiter_inside_parens()
    {
        var tok = new CssTokenizer("calc(1; 2);end".AsSpan(), null);
        var read = tok.ReadUntilAnyTopLevel(";");
        // The ';' inside calc(...) is skipped; we stop at the top-level ';' after the paren.
        Assert.Equal("calc(1; 2)", read.ToString());
        Assert.Equal(';', tok.PeekChar());
    }

    [Fact]
    public void ReadUntilAnyTopLevel_does_not_stop_at_delimiter_inside_string()
    {
        var tok = new CssTokenizer("\"a;b\";rest".AsSpan(), null);
        var read = tok.ReadUntilAnyTopLevel(";");
        Assert.Equal("\"a;b\"", read.ToString());
    }

    [Fact]
    public void SkipString_handles_escaped_quote()
    {
        var tok = new CssTokenizer("\"a\\\"b\" rest".AsSpan(), null);
        tok.SkipString();
        Assert.Equal(' ', tok.PeekChar());
    }

    [Fact]
    public void SkipRule_consumes_through_semicolon_for_statement_form()
    {
        var tok = new CssTokenizer("@charset \"UTF-8\"; rest".AsSpan(), null);
        tok.ReadAtKeyword();
        tok.SkipRule();
        // At this point we're at ' rest' (the leading space).
        tok.SkipWhitespaceAndComments();
        Assert.Equal('r', tok.PeekChar());
    }

    [Fact]
    public void SkipRule_consumes_through_curly_block_for_block_form()
    {
        var tok = new CssTokenizer(".a { color: red } .b".AsSpan(), null);
        tok.SkipRule();
        tok.SkipWhitespaceAndComments();
        Assert.Equal('.', tok.PeekChar());
    }

    [Fact]
    public void GetSubstring_returns_slice()
    {
        var tok = new CssTokenizer("abcdef".AsSpan(), null);
        Assert.Equal("bcd", tok.GetSubstring(1, 3));
    }
}
