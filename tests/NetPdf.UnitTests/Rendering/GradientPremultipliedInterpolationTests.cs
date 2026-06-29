// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>PR #237 review [P2] — gradient stop colors must be interpolated in PREMULTIPLIED RGBA
/// (CSS Images 3 §3.4.2). The shared mix (<see cref="FragmentPainter.PremulMix"/>, used by the color-hint
/// sampler) and the out-of-range boundary stop interpolation (<see cref="FragmentPainter.ColorAt"/>) are
/// alpha-weighted so a (semi-)transparent neighbor doesn't bleed its RGB into the result. Opaque-only
/// blends reduce to the plain RGB lerp (byte-identical with the prior straight-average behavior).</summary>
public sealed class GradientPremultipliedInterpolationTests
{
    [Fact]
    public void PremulMix_with_a_fully_transparent_endpoint_keeps_the_opaque_color()
    {
        // Opaque red mixed 50% with FULLY-TRANSPARENT blue: the transparent side contributes no color, so
        // the result is pure red at half alpha — NOT a washed purple (the raw-average bug).
        var (r, g, b, a) = FragmentPainter.PremulMix((255, 0, 0, 1.0), (0, 0, 255, 0.0), 0.5);
        Assert.Equal(0.5, a, 6);   // alpha = linear interpolant (1 + 0)/2
        Assert.Equal(255.0, r, 6); // R fully red (the transparent blue adds nothing)
        Assert.Equal(0.0, g, 6);
        Assert.Equal(0.0, b, 6);   // B — NO blue bleed
    }

    [Fact]
    public void PremulMix_with_equal_alpha_is_the_plain_lerp()
    {
        // Two opaque colors: premultiplied mix reduces to the straight per-channel lerp.
        var (r, g, b, a) = FragmentPainter.PremulMix((255, 0, 0, 1.0), (0, 0, 255, 1.0), 0.5);
        Assert.Equal(1.0, a, 6);
        Assert.Equal(127.5, r, 6); // (255 + 0)/2
        Assert.Equal(0.0, g, 6);
        Assert.Equal(127.5, b, 6); // (0 + 255)/2
    }

    [Fact]
    public void ColorAt_clips_an_out_of_range_transparent_stop_without_rgb_bleed()
    {
        // A transparent BLUE stop at -0.5 and an opaque RED stop at +0.5. The boundary at offset 0.0 is
        // halfway between them. Premultiplied: the transparent blue contributes no color, so the
        // boundary is pure red at 50% alpha. Raw interpolation would give a half-blue (R≈127, B≈127).
        var stops = new List<FragmentPainter.ResolvedGradientStop>
        {
            new(-0.5, R: 0,   G: 0, B: 255, A: 0.0), // transparent blue (out of range, low side)
            new( 0.5, R: 255, G: 0, B: 0,   A: 1.0), // opaque red
        };
        var (r, g, b, a) = FragmentPainter.ColorAt(stops, 0.0);
        Assert.Equal(0.5, a, 3);
        Assert.Equal(255.0, r, 3); // fully red
        Assert.Equal(0.0, g, 3);
        Assert.Equal(0.0, b, 3);   // NO blue bleed from the transparent neighbor
    }

    [Fact]
    public void ColorAt_opaque_segment_is_a_plain_linear_lerp_byte_identical()
    {
        // Both endpoints opaque → premultiplied lerp equals the straight RGB lerp (the native-shading
        // path stays byte-identical).
        var stops = new List<FragmentPainter.ResolvedGradientStop>
        {
            new(0.0, R: 0,   G: 0, B: 0, A: 1.0),
            new(1.0, R: 200, G: 0, B: 0, A: 1.0),
        };
        var (r, _, _, a) = FragmentPainter.ColorAt(stops, 0.25);
        Assert.Equal(1.0, a, 6);
        Assert.Equal(50.0, r, 6); // 0 + (200 - 0) * 0.25
    }
}
