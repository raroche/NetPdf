// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Unit tests for <see cref="PageMarginBoxPainter"/>'s pure helpers — the overflow-clipping cycle's
/// line-granularity cap (<see cref="PageMarginBoxPainter.MaxLinesThatFit"/>). The painter's layout
/// behavior is covered end-to-end by the facade tests in <c>HtmlPdfConvertTests</c>.
/// </summary>
public sealed class PageMarginBoxPainterTests
{
    [Theory]
    [InlineData(57.6, 19.2, 5, 3)]    // 3 × 19.2 = 57.6 fits exactly (the epsilon absorbs the boundary)
    [InlineData(57.0, 19.2, 5, 2)]    // just under three lines → 2 whole lines fit
    [InlineData(38.9, 19.2, 5, 2)]    // two lines + a sliver → the partial third line is dropped
    [InlineData(5.0, 19.2, 2, 0)]     // shorter than one line → nothing fits (decoration-only box)
    [InlineData(0.0, 19.2, 2, 0)]     // zero content-box height → nothing fits
    [InlineData(1000.0, 19.2, 2, 2)]  // taller than the block → clamped to the total (no over-count)
    public void MaxLinesThatFit_caps_to_the_content_box_height(
        double contentBoxHeightPx, double lineHeightPx, int totalLines, int expected)
    {
        Assert.Equal(expected,
            PageMarginBoxPainter.MaxLinesThatFit(contentBoxHeightPx, lineHeightPx, totalLines));
    }

    [Fact]
    public void MaxLinesThatFit_negative_height_keeps_no_lines()
    {
        // Defensive: a (clamped) content-box height can't go negative upstream, but a negative input
        // must not floor-divide into a bogus count.
        Assert.Equal(0, PageMarginBoxPainter.MaxLinesThatFit(-4.0, 19.2, 3));
    }

    [Fact]
    public void MaxLinesThatFit_non_positive_line_height_keeps_every_line()
    {
        // Defensive: a non-positive line-height can't have produced an overflowing block-height
        // (it would be ≤ 0 ≤ any content-box height), so the cap keeps every line rather than
        // dividing by zero.
        Assert.Equal(4, PageMarginBoxPainter.MaxLinesThatFit(57.6, 0.0, 4));
        Assert.Equal(4, PageMarginBoxPainter.MaxLinesThatFit(57.6, -1.0, 4));
    }

    [Fact]
    public void MaxLinesThatFit_ratio_beyond_int_range_keeps_every_line()
    {
        // Post-PR-#155 review P2: a ratio far beyond int.MaxValue (a tall box over a tiny positive
        // line-height) must return totalLines, NOT overflow the double→int cast into an unspecified
        // value (e.g. int.MinValue → clamped to 0 → every line wrongly clipped). The range is
        // narrowed before the cast.
        Assert.Equal(3, PageMarginBoxPainter.MaxLinesThatFit(1e18, 1e-4, 3));            // ratio ≈ 1e22
        Assert.Equal(2, PageMarginBoxPainter.MaxLinesThatFit(double.MaxValue, 1e-300, 2)); // ratio = ∞
    }
}
