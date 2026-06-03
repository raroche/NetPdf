// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Unit tests for <see cref="PageMarginBoxGeometry"/> — Phase 3 Task 21 cycle 3, mapping each of
/// the 16 CSS Page 3 §6.4 margin-box names to its page-px rectangle + alignment factors.
/// </summary>
public sealed class PageMarginBoxGeometryTests
{
    // Page 1000 × 2000 px; margins top 100, right 50, bottom 80, left 40.
    //   contentWidth = 1000 − 40 − 50 = 910; contentHeight = 2000 − 100 − 80 = 1820.
    //   rightX = 950; bottomY = 1920.
    private const double PageW = 1000, PageH = 2000, MTop = 100, MRight = 50, MBottom = 80, MLeft = 40;

    [Theory]
    // name, X, Y, Width, Height, HAlign, VAlign
    [InlineData("top-center", 40, 0, 910, 100, 0.5, 0.5)]
    [InlineData("top-left", 40, 0, 910, 100, 0.0, 0.5)]
    [InlineData("top-right", 40, 0, 910, 100, 1.0, 0.5)]
    [InlineData("bottom-center", 40, 1920, 910, 80, 0.5, 0.5)]
    [InlineData("bottom-left", 40, 1920, 910, 80, 0.0, 0.5)]
    [InlineData("top-left-corner", 0, 0, 40, 100, 0.5, 0.5)]
    [InlineData("top-right-corner", 950, 0, 50, 100, 0.5, 0.5)]
    [InlineData("bottom-right-corner", 950, 1920, 50, 80, 0.5, 0.5)]
    [InlineData("left-top", 0, 100, 40, 1820, 0.5, 0.0)]
    [InlineData("left-middle", 0, 100, 40, 1820, 0.5, 0.5)]
    [InlineData("right-bottom", 950, 100, 50, 1820, 0.5, 1.0)]
    public void TryGetRegion_maps_names_to_rects_and_alignment(
        string name, double x, double y, double w, double h, double hAlign, double vAlign)
    {
        Assert.True(PageMarginBoxGeometry.TryGetRegion(name, PageW, PageH, MTop, MRight, MBottom, MLeft, out var r));
        Assert.Equal(x, r.X, 3);
        Assert.Equal(y, r.Y, 3);
        Assert.Equal(w, r.Width, 3);
        Assert.Equal(h, r.Height, 3);
        Assert.Equal(hAlign, r.HAlign, 3);
        Assert.Equal(vAlign, r.VAlign, 3);
    }

    [Theory]
    [InlineData("middle-center")]   // not a CSS Page 3 §6.4 box
    [InlineData("")]
    [InlineData("TOP-CENTER")]      // resolver lowercases names before lookup; this helper is exact
    public void TryGetRegion_rejects_unknown_names(string name)
    {
        Assert.False(PageMarginBoxGeometry.TryGetRegion(name, PageW, PageH, MTop, MRight, MBottom, MLeft, out _));
    }
}
