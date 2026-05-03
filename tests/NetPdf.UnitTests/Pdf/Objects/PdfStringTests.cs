// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Objects;

public sealed class PdfLiteralStringTests
{
    [Theory]
    [InlineData("Hello", "(Hello)")]
    [InlineData("", "()")]
    [InlineData("a b c", "(a b c)")]
    public void Plain_ascii_round_trips(string input, string expected)
    {
        Assert.Equal(expected, PdfBytes.Ascii(new PdfLiteralString(input)));
    }

    [Theory]
    [InlineData("a(b", @"(a\(b)")]
    [InlineData("a)b", @"(a\)b)")]
    [InlineData("a\\b", @"(a\\b)")]
    public void Special_chars_are_escaped(string input, string expected)
    {
        Assert.Equal(expected, PdfBytes.Ascii(new PdfLiteralString(input)));
    }

    [Fact]
    public void Newline_is_escaped()
    {
        Assert.Equal(@"(line1\nline2)", PdfBytes.Ascii(new PdfLiteralString("line1\nline2")));
    }

    [Fact]
    public void Non_ascii_string_throws()
    {
        Assert.Throws<ArgumentException>(() => new PdfLiteralString("café"));
    }

    [Fact]
    public void High_byte_is_octal_escaped()
    {
        // 0xA0 = octal 240
        var s = new PdfLiteralString(new byte[] { (byte)'a', 0xA0, (byte)'b' });
        Assert.Equal(@"(a\240b)", PdfBytes.Ascii(s));
    }

    [Fact]
    public void Bytes_property_returns_input()
    {
        var s = new PdfLiteralString("hi");
        Assert.Equal(new byte[] { (byte)'h', (byte)'i' }, s.Bytes.ToArray());
    }
}

public sealed class PdfHexStringTests
{
    [Fact]
    public void Empty_writes_empty_brackets()
    {
        Assert.Equal("<>", PdfBytes.Ascii(new PdfHexString(ReadOnlySpan<byte>.Empty)));
    }

    [Fact]
    public void Bytes_emit_as_hex_pairs()
    {
        var s = new PdfHexString(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Assert.Equal("<DEADBEEF>", PdfBytes.Ascii(s));
    }

    [Fact]
    public void Single_byte_emits_two_digits()
    {
        Assert.Equal("<00>", PdfBytes.Ascii(new PdfHexString(new byte[] { 0x00 })));
        Assert.Equal("<0F>", PdfBytes.Ascii(new PdfHexString(new byte[] { 0x0F })));
        Assert.Equal("<F0>", PdfBytes.Ascii(new PdfHexString(new byte[] { 0xF0 })));
    }

    [Fact]
    public void FromUtf16BeWithBom_prefixes_FEFF_and_encodes_big_endian()
    {
        var s = PdfHexString.FromUtf16BeWithBom("Hi");
        // FE FF + 00 48 + 00 69
        Assert.Equal("<FEFF00480069>", PdfBytes.Ascii(s));
    }

    [Fact]
    public void FromUtf16BeWithBom_handles_unicode()
    {
        var s = PdfHexString.FromUtf16BeWithBom("café");
        // BOM (FEFF) + c=0063 a=0061 f=0066 é=00E9
        Assert.Equal("<FEFF00630061006600E9>", PdfBytes.Ascii(s));
    }
}
