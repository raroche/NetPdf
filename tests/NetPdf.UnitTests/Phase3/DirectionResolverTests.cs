// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Direction pipeline (PR 2 task 4) + RTL text-align swap (task 5). The shared
/// <c>direction</c> resolution maps a box's computed <c>direction</c> onto the bidi
/// paragraph base direction and the inline-start ↔ left/right mapping that
/// <c>text-align</c> consumes. Covers the readers (<c>ReadDirection</c> /
/// <c>IsRtl</c> / <c>ReadParagraphDirection</c>) and the direction-relative
/// <c>start</c>/<c>end</c> swap in <c>ReadInlineAlignFactor</c>.
/// </summary>
public sealed class DirectionResolverTests
{
    private static ComputedStyle Style() => ComputedStyle.RentForExclusiveTesting();

    private static ComputedStyle StyleWith(int? direction = null, int? textAlign = null)
    {
        var s = Style();
        if (direction is { } d) s.Set(PropertyId.Direction, ComputedSlot.FromKeyword(d));
        if (textAlign is { } t) s.Set(PropertyId.TextAlign, ComputedSlot.FromKeyword(t));
        return s;
    }

    // ---- readers (task 4) ----

    [Fact]
    public void Default_direction_is_ltr()
    {
        // No explicit `direction` → the slot is Unset → the reader defaults to ltr (the
        // CSS initial value), so an unstyled block is byte-identical to the pre-pipeline path.
        var s = Style();
        Assert.Equal(InlineDirection.Ltr, s.ReadDirection());
        Assert.False(s.IsRtl());
        Assert.Equal(ParagraphDirection.LeftToRight, s.ReadParagraphDirection());
    }

    [Fact]
    public void Direction_ltr_keyword_reads_ltr()
    {
        var s = StyleWith(direction: 0);
        Assert.Equal(InlineDirection.Ltr, s.ReadDirection());
        Assert.False(s.IsRtl());
        Assert.Equal(ParagraphDirection.LeftToRight, s.ReadParagraphDirection());
    }

    [Fact]
    public void Direction_rtl_keyword_reads_rtl_and_maps_to_bidi_rtl()
    {
        // `direction: rtl` (keyword id 1) → InlineDirection.Rtl → bidi paragraph base
        // direction RightToLeft (UAX #9 level 1), the value threaded to LayoutPerRun.
        var s = StyleWith(direction: 1);
        Assert.Equal(InlineDirection.Rtl, s.ReadDirection());
        Assert.True(s.IsRtl());
        Assert.Equal(ParagraphDirection.RightToLeft, s.ReadParagraphDirection());
    }

    // ---- text-align start/end swap (task 5) ----
    // Keyword ids: start=0, end=1, left=2, right=3, center=4, justify=5, match-parent=6, justify-all=7.

    [Theory]
    [InlineData(0, 0.0)]   // start → left edge (the initial)
    [InlineData(1, 1.0)]   // end → right edge
    [InlineData(2, 0.0)]   // left (physical)
    [InlineData(3, 1.0)]   // right (physical)
    [InlineData(4, 0.5)]   // center
    [InlineData(5, 0.0)]   // justify — distributed, no whole-line shift
    [InlineData(6, 0.0)]   // match-parent → start → left
    [InlineData(7, 0.0)]   // justify-all — distributed
    public void Ltr_align_factor_is_physical(int textAlign, double expected)
    {
        var s = StyleWith(direction: 0, textAlign: textAlign);
        Assert.Equal(expected, s.ReadInlineAlignFactor(), precision: 3);
    }

    [Theory]
    [InlineData(0, 1.0)]   // start → RIGHT edge in RTL (the swap)
    [InlineData(1, 0.0)]   // end → LEFT edge in RTL (the swap)
    [InlineData(2, 0.0)]   // left STAYS physical-left under RTL
    [InlineData(3, 1.0)]   // right STAYS physical-right under RTL
    [InlineData(4, 0.5)]   // center is symmetric
    [InlineData(5, 0.0)]   // justify distributes regardless of direction
    [InlineData(6, 1.0)]   // match-parent → start → RIGHT in RTL
    [InlineData(7, 0.0)]   // justify-all distributes
    public void Rtl_align_factor_swaps_start_end(int textAlign, double expected)
    {
        var s = StyleWith(direction: 1, textAlign: textAlign);
        Assert.Equal(expected, s.ReadInlineAlignFactor(), precision: 3);
    }

    [Fact]
    public void Rtl_default_text_align_start_right_aligns()
    {
        // The headline behavior of tasks 4+5: a `direction: rtl` block with NO author
        // text-align (the initial `start`) RIGHT-aligns its content (factor 1.0), where
        // the same default in LTR left-aligns (factor 0.0).
        var rtl = StyleWith(direction: 1);   // text-align unset → start
        var ltr = StyleWith(direction: 0);
        Assert.Equal(1.0, rtl.ReadInlineAlignFactor(), precision: 3);
        Assert.Equal(0.0, ltr.ReadInlineAlignFactor(), precision: 3);
    }
}
