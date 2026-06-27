// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using SkiaSharp;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>Phase 4 shadows — the blurred <c>text-shadow</c> raster bridge. Builds an
/// <see cref="SKTypeface"/> from font bytes, unions the run's glyph outlines, blurs, and produces an
/// RGB + <c>/SMask</c> image XObject; over-cap / empty runs return null.</summary>
public sealed class TextShadowRasterizerTests
{
    /// <summary>Real, Skia-parseable single-face font bytes + a glyph id for 'A'.</summary>
    private static byte[] FontBytes(out ushort glyphA)
    {
        using var tf = SKTypeface.Default;
        glyphA = tf.GetGlyph('A');
        using var stream = tf.OpenStream(out _);
        var buffer = new byte[stream.Length];
        stream.Read(buffer, buffer.Length);
        return buffer;
    }

    [Fact]
    public void Empty_glyph_run_returns_null()
    {
        var img = TextShadowRasterizer.TryRasterizeGlyphRun(
            new byte[] { 1, 2, 3 }, System.ReadOnlySpan<ushort>.Empty, 16f, 4f, 0, 0, 0, 1, 2.0,
            out var ox, out var oy, out var w, out var h);
        Assert.Null(img);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    [Fact]
    public void Fully_transparent_color_returns_null()
    {
        var bytes = FontBytes(out var gid);
        var img = TextShadowRasterizer.TryRasterizeGlyphRun(
            bytes, new[] { gid }, 16f, 4f, 0, 0, 0, /*a*/0, 2.0, out _, out _, out _, out _);
        Assert.Null(img);
    }

    [Fact]
    public void Blurred_glyph_run_produces_an_image_with_an_smask()
    {
        var bytes = FontBytes(out var gid);
        var img = TextShadowRasterizer.TryRasterizeGlyphRun(
            bytes, new[] { gid, gid, gid }, 32f, 4f, 0.1, 0.2, 0.3, 1.0, 2.0,
            out var ox, out var oy, out var w, out var h);
        Assert.NotNull(img);
        Assert.NotNull(img!.SMask);          // per-pixel alpha plane
        Assert.True(w > 0 && h > 0);
        Assert.True(double.IsFinite(ox) && double.IsFinite(oy));
        Assert.True(oy < 0);                  // ink sits above the baseline (CSS y-down)
    }

    [Fact]
    public void A_larger_blur_grows_the_bitmap()
    {
        var bytes = FontBytes(out var gid);
        TextShadowRasterizer.TryRasterizeGlyphRun(
            bytes, new[] { gid }, 32f, 0f, 0, 0, 0, 1, 2.0, out _, out _, out var w0, out var h0);
        TextShadowRasterizer.TryRasterizeGlyphRun(
            bytes, new[] { gid }, 32f, 16f, 0, 0, 0, 1, 2.0, out _, out _, out var wBlur, out var hBlur);
        Assert.True(wBlur > w0);              // the 3σ blur margin expands the raster
        Assert.True(hBlur > h0);
    }

    [Fact]
    public void An_over_cap_run_returns_null()
    {
        var bytes = FontBytes(out var gid);
        // A font size far past the 4096 px/side cap (even at 1× scale) must fall back (null).
        var img = TextShadowRasterizer.TryRasterizeGlyphRun(
            bytes, new[] { gid }, 9000f, 0f, 0, 0, 0, 1, 1.0, out _, out _, out _, out _);
        Assert.Null(img);
    }
}
