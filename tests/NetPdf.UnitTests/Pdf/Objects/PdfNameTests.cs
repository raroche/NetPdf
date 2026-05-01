// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Objects;

public sealed class PdfNameTests
{
    [Theory]
    [InlineData("Type", "/Type")]
    [InlineData("Helvetica", "/Helvetica")]
    [InlineData("ABC123", "/ABC123")]
    [InlineData("a.b.c", "/a.b.c")]   // dots are OK
    [InlineData("a-b_c", "/a-b_c")]   // hyphen and underscore are OK
    public void Plain_ascii_name_writes_with_leading_slash(string input, string expected)
    {
        Assert.Equal(expected, PdfBytes.Ascii(new PdfName(input)));
    }

    [Theory]
    // Spaces, delimiters, and # itself must be hex-escaped per §7.3.5.
    [InlineData("Type Name", "/Type#20Name")]
    [InlineData("a/b", "/a#2Fb")]
    [InlineData("a(b", "/a#28b")]
    [InlineData("a)b", "/a#29b")]
    [InlineData("a<b", "/a#3Cb")]
    [InlineData("a>b", "/a#3Eb")]
    [InlineData("a[b", "/a#5Bb")]
    [InlineData("a]b", "/a#5Db")]
    [InlineData("a{b", "/a#7Bb")]
    [InlineData("a}b", "/a#7Db")]
    [InlineData("a%b", "/a#25b")]
    [InlineData("a#b", "/a#23b")]
    public void Delimiters_and_hash_are_hex_escaped(string input, string expected)
    {
        Assert.Equal(expected, PdfBytes.Ascii(new PdfName(input)));
    }

    [Fact]
    public void Null_or_empty_throws()
    {
        Assert.Throws<ArgumentException>(() => new PdfName(""));
        Assert.Throws<ArgumentException>(() => new PdfName(null!));
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var a = new PdfName("Type");
        var b = new PdfName("Type");
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_is_case_sensitive()
    {
        var a = new PdfName("Type");
        var b = new PdfName("type");
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Standard_names_emit_correctly()
    {
        Assert.Equal("/Type", PdfBytes.Ascii(PdfNames.Type));
        Assert.Equal("/Pages", PdfBytes.Ascii(PdfNames.Pages));
        Assert.Equal("/Catalog", PdfBytes.Ascii(PdfNames.Catalog));
        Assert.Equal("/FlateDecode", PdfBytes.Ascii(PdfNames.FlateDecode));
        Assert.Equal("/Identity-H", PdfBytes.Ascii(PdfNames.IdentityH));
    }
}
