// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>An 8-bit RGBA raster (row-major, 4 bytes/pixel: R, G, B, A) — the common currency of the
/// visual-regression harness. Both the Chrome reference PNG and the NetPdf PDF are rasterized to this so
/// <see cref="PixelDiff"/> can compare them.</summary>
public sealed record RasterImage(int Width, int Height, byte[] Rgba)
{
    /// <summary>The expected byte length for <see cref="Width"/> × <see cref="Height"/> RGBA pixels.</summary>
    public int ExpectedLength => Width * Height * 4;

    public bool SameSizeAs(RasterImage other) => Width == other.Width && Height == other.Height;

    /// <summary>Throw a clear harness error if the pixel buffer length doesn't match
    /// <see cref="Width"/>×<see cref="Height"/>×4 — a malformed rasterizer should surface a precise message,
    /// not silently truncate or crash later with <see cref="System.IndexOutOfRangeException"/>.</summary>
    public void EnsureValid()
    {
        if (Width < 0 || Height < 0)
            throw new System.ArgumentException($"negative raster dimensions: {Width}x{Height}");
        if (Rgba.Length != ExpectedLength)
            throw new System.ArgumentException(
                $"raster buffer length {Rgba.Length} != expected {ExpectedLength} for {Width}x{Height} RGBA");
    }

    /// <summary>Return the top-left <paramref name="width"/>×<paramref name="height"/> sub-raster (row-major
    /// RGBA copy). Used to reconcile a sub-pixel page-size rounding difference between the two renderers:
    /// Chrome and NetPdf can rasterize the SAME logical page size (US Letter, 792 pt) to a 1–2 px different
    /// pixel height, and cropping both to the common minimum drops only page-bottom/right margin whitespace.
    /// No-op when the requested size already equals this raster's.</summary>
    public RasterImage CropTo(int width, int height)
    {
        if (width == Width && height == Height) return this;
        if (width < 0 || height < 0 || width > Width || height > Height)
            throw new System.ArgumentException($"crop {width}x{height} out of bounds for {Width}x{Height}");
        var dst = new byte[width * height * 4];
        var srcStride = Width * 4;
        var dstStride = width * 4;
        for (var y = 0; y < height; y++)
            System.Array.Copy(Rgba, y * srcStride, dst, y * dstStride, dstStride);
        return new RasterImage(width, height, dst);
    }
}
