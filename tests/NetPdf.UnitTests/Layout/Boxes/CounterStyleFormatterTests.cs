// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Boxes;
using Xunit;

namespace NetPdf.UnitTests.Layout.Boxes;

/// <summary>
/// Direct tests for <see cref="CounterStyleFormatter"/> — the shared numeral formatter used by both
/// list-item markers and page counters (<c>counter(page, &lt;style&gt;)</c>). CSS Lists L3 §7.1.4 +
/// Counter Styles L3 §6.
/// </summary>
public sealed class CounterStyleFormatterTests
{
    [Theory]
    [InlineData(1, "1")]
    [InlineData(42, "42")]
    [InlineData(0, "0")]
    public void Decimal_is_plain(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "decimal"));

    [Theory]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(10, "10")]
    [InlineData(100, "100")]
    public void Decimal_leading_zero_pads_single_digits(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "decimal-leading-zero"));

    [Theory]
    [InlineData(1, "i")]
    [InlineData(4, "iv")]
    [InlineData(9, "ix")]
    [InlineData(40, "xl")]
    [InlineData(1990, "mcmxc")]
    [InlineData(3999, "mmmcmxcix")]
    [InlineData(0, "0")]        // out of the roman range 1..3999 → decimal
    [InlineData(4000, "4000")]
    public void Lower_roman(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "lower-roman"));

    [Theory]
    [InlineData(4, "IV")]
    [InlineData(2024, "MMXXIV")]
    public void Upper_roman(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "upper-roman"));

    [Theory]
    [InlineData(1, "a")]
    [InlineData(26, "z")]
    [InlineData(27, "aa")]
    [InlineData(28, "ab")]
    [InlineData(0, "0")]        // < 1 → decimal
    public void Lower_alpha_is_bijective_base_26(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "lower-alpha"));

    [Theory]
    [InlineData("lower-alpha", "lower-latin", 28)]   // -latin is an alias of -alpha
    [InlineData("upper-alpha", "upper-latin", 28)]
    public void Latin_aliases_alpha(string alpha, string latin, int n) =>
        Assert.Equal(CounterStyleFormatter.TryFormat(n, alpha), CounterStyleFormatter.TryFormat(n, latin));

    [Theory]
    [InlineData(1, "A")]
    [InlineData(27, "AA")]
    public void Upper_alpha(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "upper-alpha"));

    [Theory]
    [InlineData(1, "α")]
    [InlineData(24, "ω")]
    [InlineData(25, "αα")]
    public void Lower_greek_is_bijective_base_24(int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, "lower-greek"));

    [Theory]
    [InlineData("LOWER-ROMAN", 4, "iv")]   // keyword match is case-insensitive
    [InlineData("Decimal", 7, "7")]
    public void Style_names_are_case_insensitive(string style, int n, string expected) =>
        Assert.Equal(expected, CounterStyleFormatter.TryFormat(n, style));

    [Theory]
    [InlineData("hebrew")]
    [InlineData("cjk-ideographic")]
    [InlineData("georgian")]
    [InlineData("")]
    [InlineData("not-a-style")]
    public void Unsupported_styles_return_null(string style)
    {
        Assert.Null(CounterStyleFormatter.TryFormat(5, style));
        Assert.False(CounterStyleFormatter.IsSupportedStyle(style));
    }

    [Theory]
    [InlineData("decimal")]
    [InlineData("decimal-leading-zero")]
    [InlineData("lower-roman")]
    [InlineData("upper-roman")]
    [InlineData("lower-alpha")]
    [InlineData("upper-alpha")]
    [InlineData("lower-latin")]
    [InlineData("upper-latin")]
    [InlineData("lower-greek")]
    public void Supported_styles_are_recognized(string style) =>
        Assert.True(CounterStyleFormatter.IsSupportedStyle(style));
}
