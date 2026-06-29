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
}
