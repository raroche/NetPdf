// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// bg-variants cycle — the <c>background-repeat</c> / <c>-size</c> / <c>-position</c> parsers
/// (the facade tests cover the end-to-end tiling; these pin the grammar matrix).
/// </summary>
public sealed class BackgroundVariantParserTests
{
    [Theory]
    [InlineData(null, true, true)]            // unset → the initial (repeat)
    [InlineData("repeat", true, true)]
    [InlineData("no-repeat", false, false)]
    [InlineData("repeat-x", true, false)]
    [InlineData("repeat-y", false, true)]
    [InlineData("repeat no-repeat", true, false)]   // the two-value axis form
    [InlineData("no-repeat repeat", false, true)]
    public void Repeat_supported_forms_parse(string? raw, bool expectX, bool expectY)
    {
        Assert.True(FragmentPainter.TryParseBackgroundRepeat(raw, out var x, out var y));
        Assert.Equal(expectX, x);
        Assert.Equal(expectY, y);
    }

    [Theory]
    [InlineData("space")]
    [InlineData("round")]
    [InlineData("repeat space")]
    [InlineData("bogus")]
    public void Repeat_unsupported_forms_reject(string raw) =>
        Assert.False(FragmentPainter.TryParseBackgroundRepeat(raw, out _, out _));

    [Theory]
    [InlineData(null, 16, 16)]                 // unset → auto (intrinsic)
    [InlineData("auto", 16, 16)]
    [InlineData("32px 32px", 32, 32)]
    [InlineData("32px", 32, 32)]               // one value → aspect-completed (1:1 intrinsic)
    [InlineData("50% 25%", 32, 8)]             // % against the 64×32 area
    [InlineData("auto 32px", 32, 32)]          // auto side from the ratio
    [InlineData("contain", 32, 32)]            // min(64/16, 32/16) = 2 → 32×32
    [InlineData("cover", 64, 64)]              // max(4, 2) = 4 → 64×64
    public void Size_supported_forms_parse(string? raw, double expectW, double expectH)
    {
        Assert.True(FragmentPainter.TryParseBackgroundSize(
            raw, areaW: 64, areaH: 32, intrinsicW: 16, intrinsicH: 16, out var w, out var h));
        Assert.Equal(expectW, w, 3);
        Assert.Equal(expectH, h, 3);
    }

    [Theory]
    [InlineData("5em")]                        // relative units unsupported
    [InlineData("calc(10px + 2px)")]
    [InlineData("32px 32px 32px")]             // too many values
    [InlineData("bogus")]
    [InlineData("-10%")]                       // negative sizes are invalid (PR #167 review P2)
    [InlineData("-10px")]
    [InlineData("32px -10px")]
    public void Size_unsupported_forms_reject(string raw) =>
        Assert.False(FragmentPainter.TryParseBackgroundSize(
            raw, 64, 32, 16, 16, out _, out _));

    [Theory]
    [InlineData("0", 0, 0)]                    // the unitless zero is VALID (PR #167 review P2)
    [InlineData("0 0", 0, 0)]
    [InlineData("0 32px", 0, 32)]
    public void Size_zero_is_valid(string raw, double expectW, double expectH)
    {
        Assert.True(FragmentPainter.TryParseBackgroundSize(
            raw, 64, 32, 16, 16, out var w, out var h));
        Assert.Equal(expectW, w, 3);
        Assert.Equal(expectH, h, 3);
    }

    [Theory]
    [InlineData(null, 0, 0)]                   // unset → 0% 0%
    [InlineData("center", 24, 8)]              // one value → other axis centers: ((64−16)/2, (32−16)/2)
    [InlineData("left top", 0, 0)]
    [InlineData("right bottom", 48, 16)]       // (64−16, 32−16)
    [InlineData("top right", 48, 0)]           // swapped keyword pair accepted
    [InlineData("50% 50%", 24, 8)]             // the §3.6 rule: (area − tile) × %
    [InlineData("100% 0%", 48, 0)]
    [InlineData("8px 4px", 8, 4)]              // absolute lengths are plain offsets
    [InlineData("0 0", 0, 0)]                  // the unitless zero
    [InlineData("top", 24, 0)]                 // a single VERTICAL keyword = center top
    [InlineData("bottom", 24, 16)]             //   (PR #167 review P2 — the Y axis, X centers)
    [InlineData("left", 0, 8)]                 // a single horizontal keyword: Y centers
    public void Position_supported_forms_parse(string? raw, double expectX, double expectY)
    {
        Assert.True(FragmentPainter.TryParseBackgroundPosition(
            raw, areaW: 64, areaH: 32, tileW: 16, tileH: 16, out var x, out var y));
        Assert.Equal(expectX, x, 3);
        Assert.Equal(expectY, y, 3);
    }

    [Theory]
    [InlineData("left 10px top 5px")]          // 4-value edge-offset form unsupported
    [InlineData("center center center")]
    [InlineData("5em 0")]                      // relative units unsupported
    [InlineData("bogus")]
    public void Position_unsupported_forms_reject(string raw) =>
        Assert.False(FragmentPainter.TryParseBackgroundPosition(
            raw, 64, 32, 16, 16, out _, out _));
}
