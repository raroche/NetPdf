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
    // [InlineData] can't carry an internal enum member, so the rows pass the expected mode as a
    // string and the body maps it (space-round cycle — TryParseBackgroundRepeat now yields a
    // per-axis BackgroundRepeatMode, not a bool).
    private static FragmentPainter.BackgroundRepeatMode Mode(string s) => s switch
    {
        "repeat" => FragmentPainter.BackgroundRepeatMode.Repeat,
        "no-repeat" => FragmentPainter.BackgroundRepeatMode.NoRepeat,
        "space" => FragmentPainter.BackgroundRepeatMode.Space,
        "round" => FragmentPainter.BackgroundRepeatMode.Round,
        _ => throw new System.ArgumentOutOfRangeException(nameof(s), s, "unknown repeat mode"),
    };

    [Theory]
    [InlineData(null, "repeat", "repeat")]            // unset → the initial (repeat)
    [InlineData("repeat", "repeat", "repeat")]
    [InlineData("no-repeat", "no-repeat", "no-repeat")]
    [InlineData("repeat-x", "repeat", "no-repeat")]
    [InlineData("repeat-y", "no-repeat", "repeat")]
    [InlineData("space", "space", "space")]           // space-round cycle — now SUPPORTED
    [InlineData("round", "round", "round")]
    [InlineData("repeat no-repeat", "repeat", "no-repeat")]   // the two-value axis form
    [InlineData("no-repeat repeat", "no-repeat", "repeat")]
    [InlineData("repeat space", "repeat", "space")]   // two-value with a space axis
    [InlineData("space round", "space", "round")]
    public void Repeat_supported_forms_parse(string? raw, string expectX, string expectY)
    {
        Assert.True(FragmentPainter.TryParseBackgroundRepeat(raw, out var x, out var y));
        Assert.Equal(Mode(expectX), x);
        Assert.Equal(Mode(expectY), y);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("repeat diagonal")]              // invalid second-axis token
    [InlineData("repeat repeat repeat")]         // three values
    public void Repeat_unsupported_forms_reject(string raw) =>
        Assert.False(FragmentPainter.TryParseBackgroundRepeat(raw, out _, out _));

    [Fact]
    public void Axis_tiling_plan_space_packs_whole_tiles_with_equal_gaps()
    {
        // §3.2 — 88px area / 16px tile → floor(88/16) = 5 whole tiles; the 8px leftover spreads
        // as 4 equal 2px gaps → the origin step is 16 + 2 = 18px, the first tile flush at 0.
        var (first, count, step) = FragmentPainter.AxisTilingPlan(
            FragmentPainter.BackgroundRepeatMode.Space, areaPx: 88, tilePx: 16, posPx: 0,
            coverStartPx: 0, coverEndPx: 88);   // space fills the AREA, ignoring the cover window
        Assert.Equal(0.0, first, 6);
        Assert.Equal(5L, count);
        Assert.Equal(18.0, step, 6);
    }

    [Fact]
    public void Axis_tiling_plan_space_degenerates_to_a_single_positioned_tile()
    {
        // 0–1 whole tiles fit → no gaps to distribute; a single tile at the resolved position.
        var (first, count, step) = FragmentPainter.AxisTilingPlan(
            FragmentPainter.BackgroundRepeatMode.Space, areaPx: 20, tilePx: 16, posPx: 5,
            coverStartPx: 0, coverEndPx: 20);
        Assert.Equal(5.0, first, 6);
        Assert.Equal(1L, count);
        Assert.Equal(16.0, step, 6);
    }

    [Fact]
    public void Axis_tiling_plan_round_runs_pre_rescaled_tiles_edge_to_edge()
    {
        // round pre-rescales the tile (in the caller) so a whole number fits; the plan then
        // counts round(area/tile) tiles flush from the area start, step = the rescaled tile.
        var (first, count, step) = FragmentPainter.AxisTilingPlan(
            FragmentPainter.BackgroundRepeatMode.Round, areaPx: 60, tilePx: 15, posPx: 7,
            coverStartPx: 0, coverEndPx: 60);
        Assert.Equal(0.0, first, 6);
        Assert.Equal(4L, count);
        Assert.Equal(15.0, step, 6);
    }

    [Fact]
    public void Axis_tiling_plan_repeat_covers_the_clip_window_phased_at_the_origin()
    {
        // PR #170 review P1 — `repeat` tiles the PAINTING (clip) window, not just the positioning
        // area: an 80px area / 16px tile with the clip extending [−4, 84] (a 4px border strip each
        // side under a padding-box origin) → the grid (phase 0) starts at the first tile ≤ −4 (−16)
        // and runs to ≥ 84 → 7 tiles (−16,0,16,32,48,64,80) spanning [−16, 96] ⊇ [−4, 84].
        var (first, count, step) = FragmentPainter.AxisTilingPlan(
            FragmentPainter.BackgroundRepeatMode.Repeat, areaPx: 80, tilePx: 16, posPx: 0,
            coverStartPx: -4, coverEndPx: 84);
        Assert.Equal(-16.0, first, 6);
        Assert.Equal(7L, count);
        Assert.Equal(16.0, step, 6);
    }

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
    [InlineData("left 10px top 5px", 10, 5)]   // edge-offset cycle — 4-value offsets FROM the edges
    [InlineData("top 5px left 10px", 10, 5)]   // axes resolved by keyword, order-independent
    [InlineData("right 10px bottom 5px", 38, 11)] // from the FAR edges: (48−10, 16−5)
    [InlineData("left 25% top 50%", 12, 8)]    // a % offset is of the range: (48×.25, 16×.5)
    [InlineData("right 25% bottom 50%", 36, 8)] // (48−48×.25, 16−16×.5)
    [InlineData("left top 5px", 0, 5)]         // 3-value: left edge (0) + 5px from top
    [InlineData("center top 5px", 24, 5)]      // center X (48×.5) + 5px from top
    public void Position_supported_forms_parse(string? raw, double expectX, double expectY)
    {
        Assert.True(FragmentPainter.TryParseBackgroundPosition(
            raw, areaW: 64, areaH: 32, tileW: 16, tileH: 16, out var x, out var y));
        Assert.Equal(expectX, x, 3);
        Assert.Equal(expectY, y, 3);
    }

    [Theory]
    [InlineData("center center center")]       // a leftover token (3 edges, no offset to consume)
    [InlineData("left 10px right 5px")]        // two X-axis edges
    [InlineData("top 10px bottom 5px")]        // two Y-axis edges
    [InlineData("left 10px top 5px right 0")]  // 5 tokens
    [InlineData("5em 0")]                      // relative units unsupported
    [InlineData("bogus")]
    public void Position_unsupported_forms_reject(string raw) =>
        Assert.False(FragmentPainter.TryParseBackgroundPosition(
            raw, 64, 32, 16, 16, out _, out _));
}
