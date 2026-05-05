// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Runtime.InteropServices;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// An 8-byte (64-bit) computed-value slot — one per property in <see cref="ComputedStyle"/>.
/// Most CSS computed values fit in 8 bytes: a 32-bit RGBA color, a 32-bit float length in
/// pixels, a 32-bit integer or keyword id, or a 32-bit index into a side table for
/// variable-length payloads (font-family lists, gradients, transforms — added in
/// Tasks 9–10). The high byte (offset 7) holds a <see cref="ComputedSlotTag"/>
/// discriminator; the low 4 bytes hold the payload; bytes 4–6 are reserved for
/// future composite encodings.
/// </summary>
/// <remarks>
/// <para>
/// Layout uses <see cref="LayoutKind.Explicit"/> with overlay fields. All factories pack
/// their data into a single <see cref="long"/> and pass it through the private
/// <c>(long raw)</c> constructor — this avoids the
/// "second readonly-field write erases the first" trap that <see cref="LayoutKind.Explicit"/>
/// invites with multi-field constructors. The static factories shift the tag into the high
/// byte and OR the payload into the low bytes.
/// </para>
/// <para>
/// <b>Length precision.</b> Lengths are stored as <see cref="float"/> (single-precision)
/// rather than <see cref="double"/> so byte 7 stays available for the tag without colliding
/// with IEEE-754 sign/exponent bits. <see cref="float"/> gives ~7 decimal digits of
/// precision, more than enough for px lengths up to ±~16 million — orders of magnitude
/// past anything CSS layout produces.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal readonly struct ComputedSlot : IEquatable<ComputedSlot>
{
    [FieldOffset(0)] private readonly long _raw;
    [FieldOffset(0)] private readonly uint _u32;
    [FieldOffset(0)] private readonly int _i32;
    [FieldOffset(0)] private readonly float _f32;
    [FieldOffset(7)] private readonly byte _tag;

    private ComputedSlot(long raw)
    {
        // Each later assignment overwrites the previous one's bytes (overlay storage).
        // Final state: _raw fills all 8 bytes including the tag byte we packed in. The
        // earlier zero writes only exist to satisfy C#'s definite-assignment rule for
        // every readonly field.
        _u32 = 0;
        _i32 = 0;
        _f32 = 0;
        _tag = 0;
        _raw = raw;
    }

    /// <summary>Encoding discriminator. <see cref="ComputedSlotTag.Unset"/> means the slot
    /// has not been written to since the <see cref="ComputedStyle"/> was rented.</summary>
    public ComputedSlotTag Tag => (ComputedSlotTag)_tag;

    /// <summary>The unset sentinel — a slot that hasn't been written. Equivalent to a
    /// default-constructed slot (all 8 bytes = 0).</summary>
    public static ComputedSlot Unset => default;

    /// <summary><see langword="true"/> when the slot is the unset sentinel.</summary>
    public bool IsUnset => _raw == 0;

    /// <summary>Encode an RGBA color (8 bits per channel, packed 0xAARRGGBB).</summary>
    public static ComputedSlot FromColor(uint argb) =>
        new(PackTag(ComputedSlotTag.Color) | argb);

    /// <summary>Decode the RGBA color. Caller must verify <see cref="Tag"/> first.</summary>
    public uint AsColor() => _u32;

    /// <summary>Encode an absolute length in pixels.</summary>
    public static ComputedSlot FromLengthPx(double pixels) =>
        FromLengthPx((float)pixels);

    /// <summary>Encode an absolute length in pixels (single-precision).</summary>
    public static ComputedSlot FromLengthPx(float pixels) =>
        new(PackTag(ComputedSlotTag.LengthPx) | BitConverter.SingleToUInt32Bits(pixels));

    /// <summary>Decode the length in pixels.</summary>
    public double AsLengthPx() => _f32;

    /// <summary>Decode the length in pixels as the underlying single-precision storage.</summary>
    public float AsLengthPxFloat() => _f32;

    /// <summary>Encode a 32-bit signed integer.</summary>
    public static ComputedSlot FromInteger(int value) =>
        new(PackTag(ComputedSlotTag.Integer) | unchecked((uint)value));

    /// <summary>Decode the integer payload.</summary>
    public int AsInteger() => _i32;

    /// <summary>Encode a keyword id — typically a small int from a per-property keyword
    /// table built in Task 10. Distinct from <see cref="FromInteger"/> so the
    /// computed-value resolver knows the value's a keyword index, not a raw number.</summary>
    public static ComputedSlot FromKeyword(int keywordId) =>
        new(PackTag(ComputedSlotTag.Keyword) | unchecked((uint)keywordId));

    /// <summary>Decode the keyword id.</summary>
    public int AsKeyword() => _i32;

    /// <summary>Encode a percentage stored as fixed-point with 16 fractional bits — covers
    /// ±32,768% with 1/65,536% precision, more than CSS needs.</summary>
    public static ComputedSlot FromPercentage(double percentage)
    {
        var scaled = (int)Math.Round(percentage * 65536.0);
        return new(PackTag(ComputedSlotTag.Percentage) | unchecked((uint)scaled));
    }

    /// <summary>Decode the percentage (inverse of <see cref="FromPercentage"/>).</summary>
    public double AsPercentage() => _i32 / 65536.0;

    /// <summary>Encode a side-table index — used when the property's value is variable-
    /// length (font-family lists, gradient definitions, transform matrices) and must live
    /// outside the 8-byte slot. The index is interpreted by the property's specific
    /// side table, set up in Tasks 9–10.</summary>
    public static ComputedSlot FromSideTableIndex(int index) =>
        new(PackTag(ComputedSlotTag.SideTableIndex) | unchecked((uint)index));

    /// <summary>Decode the side-table index.</summary>
    public int AsSideTableIndex() => _i32;

    /// <summary>
    /// Encode arbitrary 8-byte data (escape hatch for property-specific layouts like the
    /// length+unit-tag combos used by <c>line-height</c>'s union shape). Caller supplies
    /// the discriminator tag; the high byte is overwritten regardless of its current
    /// value in <paramref name="bits"/>.
    /// </summary>
    public static ComputedSlot FromRawBits(long bits, ComputedSlotTag tag) =>
        new((bits & 0x00FF_FFFF_FFFF_FFFFL) | PackTag(tag));

    /// <summary>Raw 8-byte bits including the tag.</summary>
    public long AsRawBits() => _raw;

    private static long PackTag(ComputedSlotTag tag) => (long)(byte)tag << 56;

    public bool Equals(ComputedSlot other) => _raw == other._raw;
    public override bool Equals(object? obj) => obj is ComputedSlot s && Equals(s);
    public override int GetHashCode() => _raw.GetHashCode();
    public static bool operator ==(ComputedSlot a, ComputedSlot b) => a.Equals(b);
    public static bool operator !=(ComputedSlot a, ComputedSlot b) => !a.Equals(b);
}

/// <summary>
/// Discriminator on <see cref="ComputedSlot.Tag"/>. The Phase 2 cascade resolver
/// (Task 7) and computed-value resolver (Tasks 9–10) read the tag to dispatch
/// per-property decoding. Backed by <see cref="byte"/>; values up to 255 available.
/// </summary>
internal enum ComputedSlotTag : byte
{
    /// <summary>Slot has not been written. <see cref="ComputedSlot.IsUnset"/> reads this.</summary>
    Unset = 0,
    /// <summary>RGBA color packed 0xAARRGGBB in the low 4 bytes.</summary>
    Color = 1,
    /// <summary>Absolute length in pixels stored as <see cref="float"/>.</summary>
    LengthPx = 2,
    /// <summary>Signed 32-bit integer in the low 4 bytes.</summary>
    Integer = 3,
    /// <summary>Per-property keyword id (e.g., <c>display: block</c> → keyword 0).</summary>
    Keyword = 4,
    /// <summary>Percentage stored as fixed-point with 16 fractional bits.</summary>
    Percentage = 5,
    /// <summary>Index into a per-property side table holding variable-length data.</summary>
    SideTableIndex = 6,
    /// <summary>Property-specific composite encoding — caller-supplied 8 bytes.
    /// Reserved for shapes like <c>line-height</c>'s <c>normal | number | length</c> union.</summary>
    Composite = 7,
}
