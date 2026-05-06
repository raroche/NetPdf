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
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-2.5)]
    [InlineData(0.001)]
    [InlineData(123456.0)]
    public void Number_round_trips_through_slot(double n)
    {
        // Number storage is float32 + Number tag — distinct from LengthPx tag so the
        // decoder knows the value carries no dimension. Used by `flex-grow`,
        // `flex-shrink`, `opacity`, `line-height: <number>`, and similar.
        var slot = ComputedSlot.FromNumber(n);
        Assert.Equal(ComputedSlotTag.Number, slot.Tag);
        Assert.False(slot.IsUnset);
        Assert.Equal(n, slot.AsNumber(), 5);
    }

    [Fact]
    public void Number_rejects_NaN_and_infinity()
    {
        Assert.Throws<System.ArgumentException>(() => ComputedSlot.FromNumber(double.NaN));
        Assert.Throws<System.ArgumentException>(() => ComputedSlot.FromNumber(double.PositiveInfinity));
        Assert.Throws<System.ArgumentException>(() => ComputedSlot.FromNumber(double.NegativeInfinity));
    }

    [Fact]
    public void CurrentColor_has_dedicated_tag_and_no_payload()
    {
        // Rec 3: dedicated tag — no packed-argb sentinel that could collide with a
        // user-authored color like rgba(0, 0, 1, 0).
        var slot = ComputedSlot.CurrentColor;
        Assert.Equal(ComputedSlotTag.CurrentColor, slot.Tag);
        Assert.True(slot.IsCurrentColor);
        Assert.False(slot.IsUnset);
    }

    [Fact]
    public void CurrentColor_distinct_from_FromColor_zero()
    {
        // The old sentinel was 0x00000001 packed as a Color slot. The new
        // representation is tag-only, so there's no possible collision with any
        // ComputedSlot.FromColor(...) value.
        var cc = ComputedSlot.CurrentColor;
        var transparent = ComputedSlot.FromColor(0x00000000u);
        var nearMiss = ComputedSlot.FromColor(0x00000001u);
        Assert.NotEqual(cc, transparent);
        Assert.NotEqual(cc, nearMiss);
        Assert.NotEqual(transparent, nearMiss);
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
    public void RawBits_rejects_unset_tag()
    {
        // FromRawBits with the Unset tag would create a slot whose tag is Unset but
        // whose raw bits are non-zero — IsUnset would return false, breaking the
        // invariant that "tagged Unset" matches "default value".
        Assert.Throws<System.ArgumentException>(
            () => ComputedSlot.FromRawBits(1234L, ComputedSlotTag.Unset));
    }

    [Fact]
    public void RawBits_rejects_undefined_tag()
    {
        // Tag values past the defined enum range are rejected so the slot's encoding
        // contract stays self-describing.
        Assert.Throws<System.ArgumentException>(
            () => ComputedSlot.FromRawBits(1234L, (ComputedSlotTag)200));
    }

    // ------------------------------------------------------------
    // Factory hardening (NaN/Inf/range/negative checks)
    // ------------------------------------------------------------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromLengthPx_double_rejects_non_finite(double pixels)
    {
        Assert.Throws<System.ArgumentException>(() => ComputedSlot.FromLengthPx(pixels));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void FromLengthPx_float_rejects_non_finite(float pixels)
    {
        Assert.Throws<System.ArgumentException>(() => ComputedSlot.FromLengthPx(pixels));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromPercentage_rejects_non_finite(double percentage)
    {
        Assert.Throws<System.ArgumentException>(() => ComputedSlot.FromPercentage(percentage));
    }

    [Fact]
    public void FromPercentage_rejects_value_above_max()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => ComputedSlot.FromPercentage(ComputedSlot.MaxFixedPercentage * 2));
    }

    [Fact]
    public void FromPercentage_rejects_value_below_min()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => ComputedSlot.FromPercentage(ComputedSlot.MinFixedPercentage * 2));
    }

    [Fact]
    public void FromPercentage_accepts_boundary_values()
    {
        // Endpoints round-trip cleanly. Catches off-by-one in the range check.
        var max = ComputedSlot.FromPercentage(ComputedSlot.MaxFixedPercentage);
        Assert.Equal(ComputedSlotTag.Percentage, max.Tag);
        var min = ComputedSlot.FromPercentage(ComputedSlot.MinFixedPercentage);
        Assert.Equal(ComputedSlotTag.Percentage, min.Tag);
    }

    [Fact]
    public void FromSideTableIndex_rejects_negative()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => ComputedSlot.FromSideTableIndex(-1));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => ComputedSlot.FromSideTableIndex(int.MinValue));
    }

    [Fact]
    public void FromSideTableIndex_accepts_zero_and_positive()
    {
        Assert.Equal(0, ComputedSlot.FromSideTableIndex(0).AsSideTableIndex());
        Assert.Equal(int.MaxValue, ComputedSlot.FromSideTableIndex(int.MaxValue).AsSideTableIndex());
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
