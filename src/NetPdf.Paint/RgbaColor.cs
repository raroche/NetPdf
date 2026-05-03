// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Paint;

/// <summary>
/// Packed sRGB color stored as <c>0xRRGGBBAA</c>. Four bytes total — fits compactly inside
/// 64-byte <see cref="DisplayCommand"/> payloads. Modern wide-gamut color spaces
/// (display-p3, oklch, color-mix) are deliberately out of scope for Phase 1; when needed
/// (Phase 4) they will be modelled as a parallel "extended color" command kind rather than
/// widening this struct.
/// </summary>
internal readonly struct RgbaColor : IEquatable<RgbaColor>
{
    /// <summary>Packed channels, MSB→LSB: R, G, B, A.</summary>
    public uint Packed { get; }

    public byte R => (byte)(Packed >> 24);
    public byte G => (byte)((Packed >> 16) & 0xFF);
    public byte B => (byte)((Packed >> 8) & 0xFF);
    public byte A => (byte)(Packed & 0xFF);

    public RgbaColor(byte r, byte g, byte b, byte a = 255)
    {
        Packed = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
    }

    public RgbaColor(uint packed)
    {
        Packed = packed;
    }

    public static readonly RgbaColor Black = new(0, 0, 0);
    public static readonly RgbaColor White = new(255, 255, 255);
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);

    /// <summary>
    /// PDF DeviceRGB takes components in [0, 1]. Convert sRGB byte channels by dividing by 255.
    /// </summary>
    public (double R, double G, double B) ToNormalizedRgb()
        => (R / 255.0, G / 255.0, B / 255.0);

    /// <summary>Alpha as a normalized double in [0, 1].</summary>
    public double NormalizedAlpha => A / 255.0;

    public bool Equals(RgbaColor other) => Packed == other.Packed;
    public override bool Equals(object? obj) => obj is RgbaColor other && Equals(other);
    public override int GetHashCode() => Packed.GetHashCode();
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public static bool operator ==(RgbaColor left, RgbaColor right) => left.Equals(right);
    public static bool operator !=(RgbaColor left, RgbaColor right) => !left.Equals(right);
}
