// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.RenderingCorpus.Visual;
using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 — unit tests for the pure <see cref="PixelDiff"/> core (synthetic bitmaps; no I/O
/// / rendering). Identical images score Δ0 / SSIM 1; small uniform shifts stay within tolerance; a
/// shift at the delta limit and a structurally different image both fall out of tolerance.</summary>
public sealed class PixelDiffTests
{
    private static RasterImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
        }
        return new RasterImage(w, h, px);
    }

    private static RasterImage AddToEveryChannel(RasterImage img, int delta)
    {
        var px = (byte[])img.Rgba.Clone();
        for (var i = 0; i < px.Length; i++) px[i] = (byte)Math.Clamp(px[i] + delta, 0, 255);
        return new RasterImage(img.Width, img.Height, px);
    }

    [Fact]
    public void Identical_images_are_zero_delta_and_perfect_ssim()
    {
        var a = Solid(32, 32, 120, 130, 140);
        var r = PixelDiff.Compare(a, a);
        Assert.Equal(0, r.MaxChannelDelta);
        Assert.Equal(1.0, r.Ssim, 6);
        Assert.True(r.WithinTolerance);
    }

    [Fact]
    public void A_uniform_shift_of_three_is_within_tolerance()
    {
        var a = Solid(32, 32, 120, 130, 140);
        var b = AddToEveryChannel(a, 3);
        var r = PixelDiff.Compare(a, b);
        Assert.Equal(3, r.MaxChannelDelta);
        Assert.True(r.Ssim > 0.98);
        Assert.True(r.WithinTolerance);          // Δ 3 < 4 and SSIM high
    }

    [Fact]
    public void A_uniform_shift_of_four_fails_the_per_pixel_delta()
    {
        var a = Solid(32, 32, 120, 130, 140);
        var b = AddToEveryChannel(a, 4);
        var r = PixelDiff.Compare(a, b);
        Assert.Equal(4, r.MaxChannelDelta);
        Assert.False(r.WithinTolerance);         // Δ 4 is NOT < 4 → fails even though SSIM is high
    }

    [Fact]
    public void A_structurally_different_image_has_low_ssim()
    {
        // All mid-gray vs left-half black / right-half white — same average luma, totally different structure.
        var gray = Solid(32, 32, 128, 128, 128);
        var split = Solid(32, 32, 0, 0, 0);
        for (var y = 0; y < 32; y++)
            for (var x = 16; x < 32; x++)
            {
                var o = (y * 32 + x) * 4;
                split.Rgba[o] = split.Rgba[o + 1] = split.Rgba[o + 2] = 255;
            }
        var r = PixelDiff.Compare(gray, split);
        Assert.True(r.MaxChannelDelta > 100);
        Assert.True(r.Ssim < 0.5);
        Assert.False(r.WithinTolerance);
    }

    [Fact]
    public void A_partial_edge_window_is_handled_for_non_multiple_of_eight_sizes()
    {
        // 13×13 is not a multiple of the 8-px SSIM window → exercises the partial edge blocks.
        var a = Solid(13, 13, 200, 100, 50);
        var b = AddToEveryChannel(a, 2);
        var r = PixelDiff.Compare(a, b);
        Assert.Equal(2, r.MaxChannelDelta);
        Assert.True(r.WithinTolerance);
    }

    [Fact]
    public void A_size_mismatch_throws()
    {
        var a = Solid(8, 8, 0, 0, 0);
        var b = Solid(8, 9, 0, 0, 0);
        Assert.Throws<ArgumentException>(() => PixelDiff.Compare(a, b));
    }
}
