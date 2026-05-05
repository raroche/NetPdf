// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues;

/// <summary>
/// Round-trip tests for <see cref="ComputedSlot"/> — every typed encoding must decode
/// back to the original value and the <see cref="ComputedSlot.Tag"/> must classify
/// the slot correctly. Catches layout regressions in the explicit struct overlays.
/// </summary>
public sealed class ComputedSlotTests
{
    [Fact]
    public void Default_slot_is_unset()
    {
        var slot = default(ComputedSlot);
        Assert.True(slot.IsUnset);
        Assert.Equal(ComputedSlotTag.Unset, slot.Tag);
        Assert.Equal(ComputedSlot.Unset, slot);
    }

    [Theory]
    [InlineData(0xFF000000u)]              // opaque black
    [InlineData(0x00FFFFFFu)]              // transparent white
    [InlineData(0xFFFF0000u)]              // opaque red
    [InlineData(0x80808080u)]              // 50% gray semi-transparent
    public void Color_round_trips_through_slot(uint argb)
    {
        var slot = ComputedSlot.FromColor(argb);
        Assert.Equal(ComputedSlotTag.Color, slot.Tag);
        Assert.False(slot.IsUnset);
        Assert.Equal(argb, slot.AsColor());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(16.0)]
    [InlineData(96.0)]                     // 1 inch at 96 dpi
    [InlineData(-32.5)]                    // negative margins land here
    [InlineData(1024.5)]
    [InlineData(0.125)]                    // 1/8 — exactly representable in float32
    public void LengthPx_round_trips_through_slot(double pixels)
    {
        // Slot stores lengths as float32 (so byte 7 stays free for the tag). Test values
        // are picked to be exactly representable in float32 — values past ~7 decimal
        // digits or sub-normal doubles WILL lose precision and that's by design.
        var slot = ComputedSlot.FromLengthPx(pixels);
        Assert.Equal(ComputedSlotTag.LengthPx, slot.Tag);
        Assert.Equal(pixels, slot.AsLengthPx());
    }

    [Theory]
    [InlineData(1234567.89)]                // 7+ digit precision — float32 rounds.
    [InlineData(double.Epsilon)]            // sub-normal double → underflow to 0 in float32.
    public void LengthPx_loses_precision_past_float32_range(double pixels)
    {
        // Documents the float32 precision contract. The slot's storage gives ~7 decimal
        // digits; CSS px lengths up to ±~16 million round-trip cleanly, but extreme
        // values lose precision. Tasks 9–10 typed-value layer can opt into double
        // precision via SideTableIndex when a property genuinely needs it.
        var slot = ComputedSlot.FromLengthPx(pixels);
        Assert.Equal(ComputedSlotTag.LengthPx, slot.Tag);
        Assert.Equal((float)pixels, slot.AsLengthPxFloat());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(42)]
    public void Integer_round_trips_through_slot(int value)
    {
        var slot = ComputedSlot.FromInteger(value);
        Assert.Equal(ComputedSlotTag.Integer, slot.Tag);
        Assert.Equal(value, slot.AsInteger());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(255)]
    [InlineData(65535)]
    public void Keyword_round_trips_through_slot(int keywordId)
    {
        var slot = ComputedSlot.FromKeyword(keywordId);
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
        Assert.Equal(keywordId, slot.AsKeyword());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(50.0)]                     // 50%
    [InlineData(100.0)]
    [InlineData(33.333)]                   // fractional — fixed-point preserves to 1/65536
    [InlineData(-25.0)]
    public void Percentage_round_trips_within_fixed_point_precision(double percentage)
    {
        var slot = ComputedSlot.FromPercentage(percentage);
        Assert.Equal(ComputedSlotTag.Percentage, slot.Tag);
        Assert.Equal(percentage, slot.AsPercentage(), precision: 4);  // 16-bit fraction
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(int.MaxValue)]
    public void SideTableIndex_round_trips_through_slot(int index)
    {
        var slot = ComputedSlot.FromSideTableIndex(index);
        Assert.Equal(ComputedSlotTag.SideTableIndex, slot.Tag);
        Assert.Equal(index, slot.AsSideTableIndex());
    }

    [Fact]
    public void RawBits_with_explicit_tag_round_trip()
    {
        // A property-specific composite encoding (e.g., line-height's union of
        // normal/number/length): pack raw bits with the Composite tag and recover.
        const long bits = 0x0000_0123_4567_89ABL; // arbitrary 56-bit payload
        var slot = ComputedSlot.FromRawBits(bits, ComputedSlotTag.Composite);
        Assert.Equal(ComputedSlotTag.Composite, slot.Tag);
        Assert.Equal(bits, slot.AsRawBits() & 0x00FF_FFFF_FFFF_FFFFL);
    }

    [Fact]
    public void Different_encodings_produce_distinct_slots()
    {
        var color = ComputedSlot.FromColor(0xFF0000FFu);
        var length = ComputedSlot.FromLengthPx(0.0);
        var integer = ComputedSlot.FromInteger(0);
        // All zero-payload but different tags — must NOT compare equal.
        Assert.NotEqual(color, length);
        Assert.NotEqual(color, integer);
        Assert.NotEqual(length, integer);
    }

    [Fact]
    public void Same_encoding_with_same_payload_compares_equal()
    {
        var a = ComputedSlot.FromColor(0xFF0000FFu);
        var b = ComputedSlot.FromColor(0xFF0000FFu);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Slot_is_eight_bytes()
    {
        Assert.Equal(8, System.Runtime.InteropServices.Marshal.SizeOf<ComputedSlot>());
    }
}
