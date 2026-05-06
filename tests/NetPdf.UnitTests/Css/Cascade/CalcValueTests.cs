// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Cascade;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="CalcValue"/>'s text-formatting contract. The serialized
/// form lands back in declaration text + flows downstream to Tasks 10+; format drift
/// would silently miscompute lengths / angles / times.
/// </summary>
public sealed class CalcValueTests
{
    [Theory]
    // Unit encoded as byte to avoid xunit's "internal enum in InlineData" snag — the
    // test casts back to the internal CalcUnit. Values match CalcUnit.{Px=1,Percent=2,...}.
    [InlineData(16, (byte)1, "16px")]
    [InlineData(18.5, (byte)1, "18.5px")]
    [InlineData(50, (byte)2, "50%")]
    [InlineData(0.5, (byte)0, "0.5")]
    [InlineData(0, (byte)0, "0")]
    [InlineData(-32, (byte)1, "-32px")]
    [InlineData(45, (byte)3, "45deg")]
    [InlineData(2000, (byte)4, "2000ms")]
    public void ToCssText_renders_canonical_form(double n, byte unitByte, string expected)
    {
        var v = new CalcValue(n, (CalcUnit)unitByte);
        Assert.Equal(expected, v.ToCssText());
    }

    [Fact]
    public void Whole_doubles_drop_trailing_zero()
    {
        // 16.0 should render as "16px", not "16.0px".
        var v = new CalcValue(16.0, CalcUnit.Px);
        Assert.Equal("16px", v.ToCssText());
    }

    [Fact]
    public void Large_numbers_serialize_without_exponential_notation()
    {
        // CSS doesn't accept 1e6; expand to 1000000.
        var v = new CalcValue(1_000_000, CalcUnit.Px);
        var text = v.ToCssText();
        Assert.DoesNotContain("e", text);
        Assert.DoesNotContain("E", text);
        Assert.Contains("1000000", text);
    }
}
