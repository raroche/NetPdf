// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Objects;

public sealed class PdfBooleanTests
{
    [Fact]
    public void True_writes_true() => Assert.Equal("true", PdfBytes.Ascii(PdfBoolean.True));

    [Fact]
    public void False_writes_false() => Assert.Equal("false", PdfBytes.Ascii(PdfBoolean.False));

    [Fact]
    public void From_returns_singleton()
    {
        Assert.Same(PdfBoolean.True, PdfBoolean.From(true));
        Assert.Same(PdfBoolean.False, PdfBoolean.From(false));
    }
}

public sealed class PdfNullTests
{
    [Fact]
    public void Writes_null_keyword() => Assert.Equal("null", PdfBytes.Ascii(PdfNull.Instance));

    [Fact]
    public void Instance_is_singleton() => Assert.Same(PdfNull.Instance, PdfNull.Instance);
}

public sealed class PdfIntegerTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(-1, "-1")]
    [InlineData(42, "42")]
    [InlineData(-17, "-17")]
    [InlineData(int.MaxValue, "2147483647")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void Writes_decimal_form(long value, string expected)
    {
        Assert.Equal(expected, PdfBytes.Ascii(new PdfInteger(value)));
    }
}

public sealed class PdfRealTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.0, "1")]
    [InlineData(-1.0, "-1")]
    [InlineData(3.14, "3.14")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(0.5, "0.5")]
    [InlineData(0.123456, "0.123456")]
    [InlineData(100.0, "100")]
    public void Writes_canonical_decimal(double value, string expected)
    {
        Assert.Equal(expected, PdfBytes.Ascii(new PdfReal(value)));
    }

    [Fact]
    public void NaN_throws() =>
        Assert.Throws<ArgumentException>(() => new PdfReal(double.NaN));

    [Fact]
    public void Infinity_throws() =>
        Assert.Throws<ArgumentException>(() => new PdfReal(double.PositiveInfinity));

    [Fact]
    public void Negative_infinity_throws() =>
        Assert.Throws<ArgumentException>(() => new PdfReal(double.NegativeInfinity));
}

public sealed class PdfIndirectRefTests
{
    [Fact]
    public void Default_generation_is_zero()
    {
        Assert.Equal("3 0 R", PdfBytes.Ascii(new PdfIndirectRef(3)));
    }

    [Fact]
    public void Explicit_generation_appears()
    {
        Assert.Equal("3 5 R", PdfBytes.Ascii(new PdfIndirectRef(3, 5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Object_number_below_one_throws(int n) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfIndirectRef(n));

    [Fact]
    public void Negative_generation_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfIndirectRef(1, -1));
}
